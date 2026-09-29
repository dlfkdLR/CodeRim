using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

// Explicit opt-in for a disposable signed-out VM only. Foreground key delivery
// is not atomically bound to HWND and must never become a production Quit adapter.
internal static class QuitReopenProbe
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    internal static void Run(Process old, string package, Func<uint> activate, string output)
    {
        var oldStart = old.StartTime; var oldHandle = old.Handle;
        var window = old.MainWindowHandle;
        RequireSignedOutWindow(old, window);
        var modifiers = new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x51 };
        if (IntPtr.Size != 8 || modifiers.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0))
            throw new InvalidOperationException("The isolated Quit shortcut requires a neutral 64-bit input state.");
        using var inputDesktop = new DesktopHandle(OpenInputDesktop(0, false, 1));
        if (inputDesktop.IsInvalid || ObjectName(inputDesktop.DangerousGetHandle()) != "Default"
            || ObjectName(GetThreadDesktop(GetCurrentThreadId())) != "Default"
            || ObjectName(GetProcessWindowStation()) != "WinSta0")
            throw new InvalidOperationException("Expected the fresh runner's normal interactive input desktop.");
        var keys = new[] { Key(0x11, 0), Key(0x51, 0), Key(0x51, 2), Key(0x11, 2) };
        // Final guards immediately precede one submission. No focus manipulation,
        // modifier resets, second shortcut or forced termination is permitted.
        if (old.HasExited || old.Handle != oldHandle || old.StartTime != oldStart || GetForegroundWindow() != window
            || GetWindowThreadProcessId(window, out var pid) == 0 || pid != old.Id
            || modifiers.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0))
            throw new InvalidOperationException("Owned foreground/identity/input state changed before Quit.");
        var timer = Stopwatch.StartNew();
        Marshal.SetLastPInvokeError(0);
        var submitted = SendInput(4, keys, Marshal.SizeOf<Input>());
        var inputError = Marshal.GetLastPInvokeError();
        Write(output, "quit-input.json", new { submitted, inputError, oldPid = old.Id, oldStart,
            standardQuitRequested = submitted == 4, testerForceUsed = false, foregroundAtomicallyBound = false });
        if (submitted != 4) throw new InvalidOperationException("Quit input was incomplete. No further input or activation is allowed.");
        var exited = old.WaitForExit(15000);
        Write(output, "quit-exit.json", new { exactMainExited = exited, elapsedMs = timer.ElapsedMilliseconds,
            exitCode = exited ? (int?)old.ExitCode : null, testerForceUsed = false, internalDrainVerified = false });
        if (!exited || old.ExitCode != 0) throw new InvalidOperationException("The requested app did not exit normally within the probe bound.");
        using var reopened = Process.GetProcessById(checked((int)activate()));
        using var recorder = Process.GetCurrentProcess();
        _ = reopened.Handle;
        if (PackageName(reopened.Handle) != package || reopened.SessionId != recorder.SessionId
            || reopened.StartTime <= oldStart)
            throw new InvalidOperationException("Reopened app identity differs from the inspected package/session.");
        var ready = Stopwatch.StartNew(); var signedOut = false;
        while (ready.Elapsed < TimeSpan.FromSeconds(30) && !reopened.HasExited)
        {
            reopened.Refresh();
            if (reopened.MainWindowHandle != nint.Zero)
            {
                try { RequireSignedOutWindow(reopened, reopened.MainWindowHandle); signedOut = true; break; }
                catch (InvalidOperationException) { }
            }
            Thread.Sleep(200);
        }
        Write(output, "quit-reopen.json", new { reopened = signedOut, newPid = reopened.Id, reopened.StartTime,
            package, realAccount = false, accountSwitchVerified = false, internalDrainVerified = false,
            physicalUserPc = false, testerForceUsed = false });
        if (!signedOut) throw new InvalidOperationException("Reopened app did not expose the expected signed-out UI.");
    }
    private static void RequireSignedOutWindow(Process process, nint window)
    {
        if (window == nint.Zero || process.HasExited || GetWindowThreadProcessId(window, out var pid) == 0 || pid != process.Id)
            throw new InvalidOperationException("Expected the exact launched app's window.");
        var root = AutomationElement.FromHandle(window);
        var signedOut = root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.NameProperty, "Sign in to ChatGPT")));
        if (root.Current.ProcessId != process.Id || signedOut is null || signedOut.Current.IsOffscreen
            || process.HasExited || GetWindowThreadProcessId(window, out pid) == 0 || pid != process.Id)
            throw new InvalidOperationException("The exact app's signed-out UI is unavailable.");
    }
    private static void Write(string output, string name, object value)
        => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, Options));
    private static Input Key(ushort key, uint flags) => new() { Type = 1, Keyboard = new() { Key = key, Flags = flags } };
    private static string ObjectName(nint handle)
    {
        var characters = new char[256];
        if (handle == nint.Zero || !GetUserObjectInformation(handle, 2, characters, characters.Length * 2, out _))
            throw new InvalidOperationException("Input desktop identity could not be read.");
        var end = Array.IndexOf(characters, '\0');
        if (end <= 0) throw new InvalidOperationException("Input desktop name is invalid.");
        return new string(characters, 0, end);
    }
    private static string PackageName(nint handle)
    {
        var text = new char[512]; uint length = (uint)text.Length;
        if (GetPackageFullName(handle, ref length, text) != 0 || length is < 2 or > 512)
            throw new InvalidOperationException("Reopened app package identity is unavailable.");
        return new string(text, 0, checked((int)length - 1));
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input { [FieldOffset(0)] internal uint Type; [FieldOffset(8)] internal KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { internal ushort Key; internal ushort Scan; internal uint Flags; internal uint Time; internal nuint Extra; }
    private sealed class DesktopHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        internal DesktopHandle(nint value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle() => CloseDesktop(handle);
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(nint handle, int index, [Out] char[] data, int bytes, out int needed);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    private static extern nint GetThreadDesktop(uint thread);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    private static extern nint GetProcessWindowStation();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(nint process, ref uint length, [Out] char[] name);
}
