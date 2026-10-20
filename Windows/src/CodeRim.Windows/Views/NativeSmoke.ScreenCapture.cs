using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    // Only the explicitly requested isolated native smoke path uses desktop capture.
    // Keep this separate from RenderTargetBitmap: it records visible screen pixels,
    // without activating or repositioning a window. Inspect active/occlusion state
    // separately; a desktop rectangle is not an isolated render of this HWND.
    private static Rect CaptureScreen(Window window, string output)
    {
        Require(window.IsVisible, "Screen capture requires a visible fixture window.");
        var origin = window.PointToScreen(new Point()); var dpi = VisualTreeHelper.GetDpi(window);
        var requested = new System.Drawing.Rectangle((int)Math.Floor(origin.X), (int)Math.Floor(origin.Y),
            (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var bounds = System.Drawing.Rectangle.Intersect(requested, virtualScreen);
        File.WriteAllText(output + ".capture.json", JsonSerializer.Serialize(new {
            requested, virtualScreen, captured = bounds, fullWindowCaptured = requested == bounds,
            window.Left, window.Top, window.ActualWidth, window.ActualHeight, window.IsActive,
            dpi.DpiScaleX, dpi.DpiScaleY,
            monitors = System.Windows.Forms.Screen.AllScreens.Select(screen => new { screen.Bounds, screen.WorkingArea })
        }, JsonOptions));
        // A partially off-screen window can only supply its visible rectangle.
        // Record that limit explicitly; the caller verifies its target is inside
        // the captured pixels. This is not evidence of a full-window render.
        Require(bounds.Width > 0 && bounds.Height > 0, "The fixture window has no capturable screen pixels.");
        using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size, System.Drawing.CopyPixelOperation.SourceCopy);
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}
