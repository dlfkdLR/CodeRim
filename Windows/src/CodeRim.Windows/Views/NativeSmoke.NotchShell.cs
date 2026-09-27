using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task NotchShellRegression(DashboardWindow dashboard, NotchWindow notch, AppSettingsStore settings, string directory)
    {
        var saved = settings.Current; var observations = new List<object>();
        Exception? failure = null; var cleanup = new List<Exception>();
        void Receipt(bool completed) => File.WriteAllText(Path.Combine(directory, "windows-notch-shell.json"),
            JsonSerializer.Serialize(new { completed, fixture = true, physicalAltTab = false, observations }, JsonOptions));
        try
        {
            foreach (var edge in Enum.GetValues<NotchEdge>())
            foreach (var scale in new[] { 0.8, 1d, 1.25 })
            foreach (var atStart in new[] { false, true })
            {
                settings.Save(saved with { Edge = edge, Scale = scale, Offset = 0, ControlsPosition = atStart ? "Start" : "End",
                    ReduceMotion = true, Visibility = NotchVisibility.AlwaysShow, EnabledProviders = ["codex"] });
                await Idle();
                CheckBounds("expanded");
                var gear = Descendants<Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.settings");
                Require(gear.ToolTip is null, "Settings tooltip covers the adjacent account control.");
                notch.Activate(); gear.Focus(); await Idle();
                Require(gear.IsKeyboardFocused && notch.ControlsRevealed, "Explicit keyboard access to the tool window was lost.");
                var vertical = edge is NotchEdge.Left or NotchEdge.Right;
                var start = vertical && atStart;
                var center = gear.TranslatePoint(new Point(gear.ActualWidth / 2, gear.ActualHeight / 2), notch);
                var distance = gear.ActualWidth * .36;
                var probe = center + (edge switch
                {
                    NotchEdge.Left => new Vector(distance, start ? -distance : distance),
                    NotchEdge.Right => new Vector(-distance, start ? -distance : distance),
                    NotchEdge.Top => new Vector(distance, distance),
                    _ => new Vector(distance, -distance)
                });
                var current = TransparentSurface(notch);
                var alpha = Alpha(current, probe);
                // Sensitivity check: the old shared button template paints this
                // otherwise transparent part of the orb's larger hot zone.
                var fixedStyle = gear.Style;
                int oldAlpha;
                try
                {
                    gear.Style = (Style)notch.FindResource("IconButton"); await Idle();
                    oldAlpha = Alpha(TransparentSurface(notch), probe);
                }
                finally { gear.Style = fixedStyle; await Idle(); }
                observations.Add(new { stage = "orb-alpha", edge, scale, atStart, probe, alpha, oldAlpha,
                    keyboardFocused = gear.IsKeyboardFocused }); Receipt(false);
                Require(alpha <= 1 && oldAlpha >= 20, "Settings hot zone painted outside the rail, or the regression probe was insensitive.");
                if (scale == 1 && !atStart)
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(current));
                    using (var file = File.Create(Path.Combine(directory, "windows-notch-shell-" + edge + ".png"))) encoder.Save(file);
                    CaptureScreen(notch, Path.Combine(directory, "windows-notch-shell-" + edge + ".screen.png"));
                }
                var provider = Descendants<Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.provider.codex");
                provider.Focus(); await Idle();
                Require(provider.IsKeyboardFocused, "Provider keyboard focus was lost.");
                // The lower left of the cell has neither a ring nor text. The
                // old template paints a grey rectangle there when focused.
                var emptyCell = new Point(5, provider.ActualHeight - 8);
                var providerAlpha = Alpha(TransparentSurface(provider), emptyCell);
                var providerStyle = provider.Style; int oldProviderAlpha;
                try
                {
                    provider.Style = (Style)notch.FindResource("NotchButton"); await Idle();
                    oldProviderAlpha = Alpha(TransparentSurface(provider), emptyCell);
                }
                finally { provider.Style = providerStyle; await Idle(); }
                observations.Add(new { stage = "provider-alpha", edge, scale, atStart, providerAlpha, oldProviderAlpha }); Receipt(false);
                Require(providerAlpha == 0 && oldProviderAlpha >= 20, "Provider cell regained rectangular chrome, or its regression probe was insensitive.");
                Keyboard.ClearFocus(); dashboard.Activate();
            }
            foreach (var edge in Enum.GetValues<NotchEdge>())
            foreach (var offset in new[] { -10000d, 10000d })
            {
                settings.Save(settings.Current with { Edge = edge, Offset = offset }); await Idle(); CheckBounds("clamped");
            }
            settings.Save(settings.Current with { Visibility = NotchVisibility.Hidden }); await Idle();
            Require(!notch.IsVisible, "Hidden tool window remained visible."); CheckStyle("hidden");
            settings.Save(settings.Current with { Visibility = NotchVisibility.OnHover, Offset = 0 });
            notch.SetExpanded(false); await Idle();
            Require(notch.IsVisible && !notch.Expanded, "Reshow did not restore the visible folded notch.");
            CheckBounds("folded-after-reshow");
            settings.Save(settings.Current with { Visibility = NotchVisibility.AlwaysShow }); await Idle();
            Require(notch.IsVisible && notch.Expanded, "Reshown notch did not expand."); CheckBounds("expanded-after-reshow");
            Receipt(true);

            void CheckStyle(string stage)
            {
                var handle = new WindowInteropHelper(notch).Handle;
                var styles = ShellWindowStyle(handle, -20).ToInt64();
                observations.Add(new { stage, extendedStyle = styles, toolWindow = (styles & 0x80) != 0,
                    appWindow = (styles & 0x40000) != 0, layered = (styles & 0x80000) != 0 }); Receipt(false);
                Require((styles & 0x80) != 0 && (styles & 0x40000) == 0 && (styles & 0x80000) != 0,
                    "Notch HWND must remain a layered tool window, excluded from the task switcher.");
            }
            void CheckBounds(string stage)
            {
                CheckStyle(stage);
                var handle = new WindowInteropHelper(notch).Handle;
                Require(WindowBounds(handle, out var bounds), "Notch HWND has no bounds.");
                var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(x => x.DeviceName == settings.Current.Display)
                    ?? System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
                var area = screen.WorkingArea; var dpi = VisualTreeHelper.GetDpi(notch);
                var attached = settings.Current.Edge switch
                {
                    NotchEdge.Left => bounds.Left == area.Left, NotchEdge.Right => bounds.Right == area.Right,
                    NotchEdge.Top => bounds.Top == area.Top, _ => bounds.Bottom == area.Bottom
                };
                var fits = bounds.Left >= area.Left && bounds.Top >= area.Top && bounds.Right <= area.Right && bounds.Bottom <= area.Bottom;
                var widthMatches = Math.Abs(bounds.Right - bounds.Left - Math.Ceiling(notch.Width * dpi.DpiScaleX)) <= 1;
                var heightMatches = Math.Abs(bounds.Bottom - bounds.Top - Math.Ceiling(notch.Height * dpi.DpiScaleY)) <= 1;
                observations.Add(new { stage, notch.IsVisible, notch.Expanded, settings.Current.Edge, settings.Current.Scale, settings.Current.Offset,
                    bounds = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom }, area,
                    dpi.DpiScaleX, dpi.DpiScaleY, attached, fits, widthMatches, heightMatches }); Receipt(false);
                Require(attached && fits && widthMatches && heightMatches, "Notch native geometry is detached, offscreen or scaled twice.");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { Keyboard.ClearFocus(); settings.Save(saved); dashboard.Activate(); await Idle(); }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
        }
        if (failure is not null && cleanup.Count > 0) throw new AggregateException("Notch shell fixture and cleanup failed.", new[] { failure }.Concat(cleanup));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanup.Count > 0) throw new AggregateException("Notch shell cleanup failed.", cleanup);
    }
    private static RenderTargetBitmap TransparentSurface(FrameworkElement notch)
    {
        notch.UpdateLayout();
        var bounds = new Rect(0, 0, Math.Ceiling(notch.ActualWidth), Math.Ceiling(notch.ActualHeight));
        var offset = VisualTreeHelper.GetOffset(notch);
        var brush = new VisualBrush(notch) { ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(offset.X, offset.Y, bounds.Width, bounds.Height), ViewportUnits = BrushMappingMode.Absolute,
            Viewport = bounds, Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen()) draw.DrawRectangle(brush, null, bounds);
        var bitmap = new RenderTargetBitmap((int)bounds.Width, (int)bounds.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); return bitmap;
    }
    private static int Alpha(BitmapSource bitmap, Point point)
    {
        var x = (int)Math.Round(point.X); var y = (int)Math.Round(point.Y);
        Require(x >= 0 && y >= 0 && x < bitmap.PixelWidth && y < bitmap.PixelHeight, "Alpha probe is outside the notch.");
        byte[] pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel[3];
    }
#pragma warning disable SYSLIB1054
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr ShellWindowStyle(IntPtr handle, int index);
#pragma warning restore SYSLIB1054
}
