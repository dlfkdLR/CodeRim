using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task WindowFrameRegression(DashboardStore store, AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var path = Path.Combine(CompanionFile.DataDirectory, "settings-window.json");
        var saved = File.Exists(path) ? File.ReadAllBytes(path) : null;
        DashboardWindow? window = null; Exception? failure = null; var cleanup = new List<Exception>();
        var cases = new List<object>();
        try
        {
            foreach (var scenario in new[] { "default", "offscreen", "oversized" })
            {
                if (scenario == "default") File.Delete(path);
                else File.WriteAllText(path, JsonSerializer.Serialize(new { Left = scenario == "offscreen" ? -10000d : 20d,
                    Top = scenario == "offscreen" ? -10000d : 20d, Width = 3500d, Height = 2500d, SidebarWidth = 224d }));
                window = new DashboardWindow(store, settings, vault); window.Show(); await Idle();
                Check(scenario);
                if (scenario == "oversized")
                {
                    window.WindowState = WindowState.Maximized; await Idle();
                    window.WindowState = WindowState.Normal; await Idle(); Check("restored");
                }
                window.Close(); window = null;
            }
            File.WriteAllText(Path.Combine(directory, "windows-settings-frame.json"), JsonSerializer.Serialize(new { completed = true, cases }, JsonOptions));

            void Check(string scenario)
            {
                var shown = window ?? throw new InvalidOperationException("Missing frame fixture.");
                var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(shown).Handle).WorkingArea;
                var dpi = VisualTreeHelper.GetDpi(shown);
                var origin = shown.PointToScreen(new Point());
                var bounds = new Rect(origin, new Size(shown.ActualWidth * dpi.DpiScaleX, shown.ActualHeight * dpi.DpiScaleY));
                // Allow fractional-DIP rounding within one physical pixel.
                var workArea = new Rect(area.Left - 1, area.Top - 1, area.Width + 2, area.Height + 2);
                cases.Add(new { scenario, bounds, workArea, dpi.DpiScaleX, dpi.DpiScaleY, shown.MinWidth, shown.MinHeight });
                File.WriteAllText(Path.Combine(directory, "windows-settings-frame-progress.json"), JsonSerializer.Serialize(cases, JsonOptions));
                Capture(shown, Path.Combine(directory, "windows-settings-frame-" + scenario + ".png"));
                Require(workArea.Contains(bounds), "Settings window extends beyond the selected display's working area: " + scenario);
                Require(shown.MinWidth <= area.Width / dpi.DpiScaleX && shown.MinHeight <= area.Height / dpi.DpiScaleY,
                    "Window minimum prevents fitting the display: " + scenario);
                var pixels = CaptureScreen(shown, Path.Combine(directory, "windows-settings-frame-" + scenario + ".screen.png"));
                Require(pixels.Width >= Math.Floor(bounds.Width) && pixels.Height >= Math.Floor(bounds.Height),
                    "The fitted settings window is still clipped in the desktop capture: " + scenario);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { window?.Close(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            try { if (saved is null) File.Delete(path); else File.WriteAllBytes(path, saved); }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
        }
        if (failure is not null && cleanup.Count > 0) throw new AggregateException("Window frame fixture and cleanup failed.", new[] { failure }.Concat(cleanup));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanup.Count > 0) throw new AggregateException("Window frame cleanup failed.", cleanup);
    }
}
