using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

// Read-only AX discovery on a fresh GitHub-hosted Windows VM. This probe does not
// invoke menus, type keys, sign in, call models, or terminate the app.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            args.Length != 3) return 2;
        var fullName = args[0]; var appId = args[1]; var output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(output);
        try
        {
            var activation = (IApplicationActivationManager)new ApplicationActivationManager();
            uint pid;
            try { Marshal.ThrowExceptionForHR(activation.ActivateApplication(appId, "--force-renderer-accessibility", 2, out pid)); }
            finally { Marshal.FinalReleaseComObject(activation); }
            using var process = Process.GetProcessById(checked((int)pid));
            using var recorder = Process.GetCurrentProcess();
            var handle = process.Handle; // retain the actual launched process, not just a PID
            var package = PackageName(handle);
            if (package != fullName || process.SessionId != recorder.SessionId)
                throw new InvalidOperationException("Activated process identity does not match the inspected package/session.");
            var image = process.MainModule?.FileName ?? throw new InvalidOperationException("Process image is unavailable.");
            File.WriteAllText(Path.Combine(output, "process.json"), JsonSerializer.Serialize(new {
                pid, package, image, process.StartTime, appId, realAccount = false, actionsInvoked = false
            }, Options));
            var timer = Stopwatch.StartNew();
            List<nint> windows;
            do
            {
                if (process.HasExited) throw new InvalidOperationException("Official app exited before UI inspection.");
                windows = Windows(pid);
                if (windows.Count > 0) break;
                Thread.Sleep(200);
            } while (timer.Elapsed < TimeSpan.FromSeconds(40));
            if (windows.Count == 0) throw new InvalidOperationException("No visible app window appeared.");
            // Repeated read-only samples allow initial accessibility hydration.
            var contentSamples = 0;
            for (var sample = 0; sample < 3; sample++)
            {
                if (process.HasExited || PackageName(handle) != fullName) throw new InvalidOperationException("App changed during inspection.");
                windows = Windows(pid);
                if (windows.Count == 0 || windows.Count > 8) throw new InvalidOperationException("App windows changed during inspection.");
                var snapshots = new List<object>(); var hasContent = false;
                foreach (var window in windows)
                {
                    var root = AutomationElement.FromHandle(window);
                    RequireWindow(process, window, root, pid);
                    var snapshot = Snapshot(root);
                    RequireWindow(process, window, root, pid);
                    snapshots.Add(new { hwnd = window.ToInt64(), nodes = snapshot.Nodes, contentHydrated = snapshot.HasContent });
                    hasContent |= snapshot.HasContent;
                }
                if (hasContent) contentSamples++;
                File.WriteAllText(Path.Combine(output, $"uia-{sample}.json"), JsonSerializer.Serialize(snapshots, Options));
                if (sample < 2) Thread.Sleep(2000);
            }
            if (contentSamples == 0) throw new InvalidOperationException("No hydrated app document with controls was observed.");
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new {
                inspectionCompleted = true, quitVerified = false, accountSwitchVerified = false,
                contentSamples, physicalUserPc = false, actionsInvoked = false
            }, Options));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            try { File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString()); }
            catch (Exception receiptError) { Console.Error.WriteLine("Error receipt could not be written: " + receiptError); }
            return 1;
        }
    }
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static void RequireWindow(Process process, nint window, AutomationElement root, uint pid)
    {
        if (process.HasExited || GetWindowThreadProcessId(window, out var owner) == 0 || owner != pid || root.Current.ProcessId != pid)
            throw new InvalidOperationException("Window ownership changed during inspection.");
    }
    private static (object[] Nodes, bool HasContent) Snapshot(AutomationElement root)
    {
        var queue = new Queue<(AutomationElement Element, int Depth, int Parent)>(); queue.Enqueue((root, 0, -1));
        var nodes = new List<object>(); var timer = Stopwatch.StartNew(); var hasDocument = false; var hasButton = false;
        while (queue.TryDequeue(out var item))
        {
            if (nodes.Count >= 2048 || timer.Elapsed > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("Accessibility snapshot exceeded its bound.");
            var data = item.Element.Current; var index = nodes.Count;
            hasDocument |= data.ControlType == ControlType.Document;
            hasButton |= data.ControlType == ControlType.Button;
            static string Bound(string text) => text.Length > 300 ? text[..300] : text;
            static double? Finite(double value) => double.IsFinite(value) ? value : null;
            nodes.Add(new {
                index, parent = item.Parent, depth = item.Depth,
                name = Bound(data.Name), id = Bound(data.AutomationId), type = data.ControlType.ProgrammaticName,
                accelerator = Bound(data.AcceleratorKey), accessKey = Bound(data.AccessKey),
                className = Bound(data.ClassName), framework = data.FrameworkId,
                data.IsOffscreen, data.IsEnabled, data.ProcessId,
                bounds = new { x = Finite(data.BoundingRectangle.X), y = Finite(data.BoundingRectangle.Y),
                    width = Finite(data.BoundingRectangle.Width), height = Finite(data.BoundingRectangle.Height) },
                patterns = item.Element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName).ToArray()
            });
            if (item.Depth >= 24) continue;
            for (var child = TreeWalker.ControlViewWalker.GetFirstChild(item.Element); child is not null;
                child = TreeWalker.ControlViewWalker.GetNextSibling(child))
            {
                if (nodes.Count + queue.Count >= 2048) throw new InvalidOperationException("Accessibility tree exceeded its node bound.");
                queue.Enqueue((child, item.Depth + 1, index));
            }
        }
        return (nodes.ToArray(), nodes.Count > 2 && hasDocument && hasButton);
    }
    private static List<nint> Windows(uint pid)
    {
        var result = new List<nint>();
        EnumWindows((window, _) => {
            if (GetWindowThreadProcessId(window, out var owner) != 0 && owner == pid && IsWindowVisible(window)) result.Add(window);
            return true;
        }, nint.Zero);
        return result;
    }
    private static string PackageName(nint process)
    {
        uint size = 0; var result = GetPackageFullName(process, ref size, null);
        if (result != 122 || size is 0 or > 512) throw new InvalidOperationException("Expected a packaged process.");
        var text = new char[checked((int)size)];
        if (GetPackageFullName(process, ref size, text) != 0) throw new InvalidOperationException("Package identity query failed.");
        var end = Array.IndexOf(text, '\0');
        if (end <= 0) throw new InvalidOperationException("Invalid package identity.");
        return new string(text, 0, end);
    }
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }
    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appId, nint items,
            [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appId, nint items, out uint processId);
    }
    private delegate bool WindowCallback(nint window, nint context);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowCallback callback, nint context);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(nint process, ref uint length, [Out] char[]? name);
}
