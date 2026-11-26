using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    // Hold this synthetic window steady with its dispatcher running while the probe takes a frame.
    private static async Task OfferCompositorWindow(Window fixture, string directory)
    {
        var prefix = Path.Combine(directory, "account-compositor");
        if (!File.Exists(prefix + ".request")) return;
        var handle = new WindowInteropHelper(fixture).Handle;
        if (handle == IntPtr.Zero || WindowProcess(handle, out var pid) == 0 || pid != Environment.ProcessId) return;
        bool raise = !fixture.Topmost && RuntimeInformation.OSArchitecture == Architecture.Arm64 &&
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        try
        {
            if (raise)
            {
                fixture.Topmost = true;
                fixture.Activate();
                await Idle();
            }
            var watch = Stopwatch.StartNew();
            File.WriteAllText(prefix + ".tmp", JsonSerializer.Serialize(new { pid, hwnd = handle.ToInt64(), raised = raise }));
            File.Move(prefix + ".tmp", prefix + ".ready", true);
            while (!File.Exists(prefix + ".done") && watch.Elapsed < TimeSpan.FromSeconds(50)) await Task.Delay(40);
        }
        finally
        {
            if (raise) fixture.Topmost = false;
            File.Delete(prefix + ".ready");
        }
    }
}
