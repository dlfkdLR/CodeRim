using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    // The optional test process is launched by the MSI harness. Hold this synthetic
    // account screen steady with its dispatcher running while the probe takes one frame.
    private static async Task OfferCompositorWindow(Window fixture, string directory)
    {
        var prefix = Path.Combine(directory, "account-compositor");
        if (!File.Exists(prefix + ".request")) return;
        var handle = new WindowInteropHelper(fixture).Handle;
        if (handle == IntPtr.Zero || WindowProcess(handle, out var pid) == 0 || pid != Environment.ProcessId) return;
        var watch = Stopwatch.StartNew();
        File.WriteAllText(prefix + ".tmp", JsonSerializer.Serialize(new { pid, hwnd = handle.ToInt64() }));
        File.Move(prefix + ".tmp", prefix + ".ready", true);
        try
        {
            while (!File.Exists(prefix + ".done") && watch.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(40);
        }
        finally { File.Delete(prefix + ".ready"); }
    }
}
