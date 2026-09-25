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
        var bounds = new System.Drawing.Rectangle((int)Math.Floor(origin.X), (int)Math.Floor(origin.Y),
            (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        Require(bounds.Width > 0 && bounds.Height > 0 && System.Windows.Forms.SystemInformation.VirtualScreen.Contains(bounds),
            "The fixture window is outside the capturable virtual screen.");
        using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size, System.Drawing.CopyPixelOperation.SourceCopy);
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}
