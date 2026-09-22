using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;

namespace CodeRim.ProcessFixture;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var root = Environment.GetEnvironmentVariable("SYNTHETIC_RUN_DIR") ?? throw new InvalidOperationException("Fixture root is required.");
        Directory.CreateDirectory(root);
        if (args.FirstOrDefault() != "child") File.WriteAllText(Path.Combine(root, "root.pid"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        switch (args.FirstOrDefault())
        {
            case "echo":
                Console.OutputEncoding = new UTF8Encoding(false);
                foreach (var argument in args.Skip(1)) Console.WriteLine(argument);
                Console.WriteLine(Environment.GetEnvironmentVariable("SYNTHETIC_UNICODE"));
                Console.Error.Write("fixture-error");
                return Console.In.ReadToEnd().Length == 0 ? 0 : 9;
            case "flood":
                File.WriteAllText(Path.Combine(root, "flood.started"), "started");
                using (var output = Console.OpenStandardOutput())
                {
                    var bytes = new byte[32]; Array.Fill(bytes, (byte)'x');
                    while (true) output.Write(bytes);
                }
            case "early":
            case "early-detached":
            case "waiting":
                var executable = Environment.GetEnvironmentVariable("SYNTHETIC_FIXTURE_BIN") ?? throw new InvalidOperationException("Fixture binary is required.");
                if (args[0] == "early-detached" && OperatingSystem.IsWindows())
                    foreach (var standard in new[] { -10, -11, -12 })
                        if (!SetHandleInformation(GetStdHandle(standard), 1, 0)) throw new System.ComponentModel.Win32Exception();
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = args[0] == "early-detached", RedirectStandardOutput = args[0] == "early-detached",
                    RedirectStandardError = args[0] == "early-detached" };
                start.ArgumentList.Add("child"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using (var child = Process.Start(start) ?? throw new IOException("Child fixture failed."))
                {
                    File.WriteAllText(Path.Combine(root, "child.pid"), child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "|" + child.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var ready = Stopwatch.StartNew();
                    while (!File.Exists(Path.Combine(root, "child.started")))
                    {
                        if (child.HasExited || ready.Elapsed > TimeSpan.FromSeconds(2)) throw new IOException("Child did not acknowledge startup.");
                        await Task.Delay(10).ConfigureAwait(false);
                    }
                }
                File.WriteAllText(Path.Combine(root, "parent.started"), "started");
                if (args[0] == "waiting") await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                return 0;
            case "child":
                // Do not overwrite the parent marker with this descendant's own PID.
                using (var self = Process.GetCurrentProcess())
                    File.WriteAllText(Path.Combine(root, "child.self"), self.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "|" + self.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "|" + (Environment.GetEnvironmentVariable("SYNTHETIC_TEST_ID") ?? ""));
                try
                {
                    using var parent = Process.GetProcessById(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
                    File.WriteAllText(Path.Combine(root, "child.started"), "started");
                    if (parent.WaitForExit(2000) && parent.ExitCode == 0)
                        File.WriteAllText(Path.Combine(root, "parent.normal-exit-observed"), "0");
                }
                catch (ArgumentException) { File.WriteAllText(Path.Combine(root, "parent.already-exited"), "observed"); }
                await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                return 0;
            case "sibling":
                File.WriteAllText(Path.Combine(root, "sibling.started"), "started");
                await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                return 0;
            default: return 2;
        }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint GetStdHandle(int kind);
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
}
