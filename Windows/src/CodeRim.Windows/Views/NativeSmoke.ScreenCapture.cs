using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Bitmap = System.Drawing.Bitmap;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Rectangle = System.Drawing.Rectangle;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private readonly record struct DesktopCapture(Rect Geometry, bool Verified);

    private static readonly string[] DesktopCaptureNames = [
        "windows-account-history-overview",
        "windows-notch-shell-Left", "windows-notch-shell-Right", "windows-notch-shell-Top", "windows-notch-shell-Bottom",
        "windows-settings-frame-default", "windows-settings-frame-offscreen", "windows-settings-frame-oversized",
        "windows-settings-frame-restored", "windows-settings-frame-caption-restored"
    ];

    private static void ClearDesktopCaptures(string directory)
    {
        // A stopped rerun must not publish captures it never reached.
        foreach (var name in DesktopCaptureNames)
        {
            var file = Path.Combine(directory, name + ".screen.png");
            File.Delete(file);
            File.Delete(file + ".capture.json");
        }
    }

    // An active WPF window may still be covered by setup or a different desktop.
    // Save only owner-rendered pixels after a physical-screen comparison. The
    // separate WPF .png and geometry remain available if this check cannot run.
    private static DesktopCapture CaptureScreen(Window window, string output)
    {
        File.Delete(output); // A previous run must not survive an unverified attempt.
        File.Delete(output + ".capture.json");
        Require(window.IsVisible, "Screen capture requires a visible fixture window.");
        var origin = window.PointToScreen(new System.Windows.Point());
        var dpi = VisualTreeHelper.GetDpi(window);
        var requested = new Rectangle((int)Math.Floor(origin.X), (int)Math.Floor(origin.Y),
            (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var bounds = Rectangle.Intersect(requested, virtualScreen);
        Require(bounds.Width > 0 && bounds.Height > 0, "The fixture window has no capturable screen pixels.");
        var handle = new WindowInteropHelper(window).Handle;
        var reason = "fixture-hwnd-unavailable";
        if (handle != IntPtr.Zero && WindowProcess(handle, out var owner) != 0 && owner == (uint)Environment.ProcessId)
        {
            try { reason = VerifyDesktopPixels(handle, requested, bounds, output); }
            catch (System.ComponentModel.Win32Exception) { reason = "desktop-read-unavailable"; }
            catch (ExternalException) { reason = "native-render-unavailable"; }
        }
        var verified = reason == "verified";
        if (!verified) File.Delete(output);
        File.WriteAllText(output + ".capture.json", JsonSerializer.Serialize(new {
            captureStatus = reason, screenPixelsVerified = verified, method = "PrintWindow client / desktop exact RGB",
            requested, virtualScreen, visibleBounds = bounds, captured = verified ? (Rectangle?)bounds : null,
            fullWindowCaptured = verified && requested == bounds,
            window.Left, window.Top, window.ActualWidth, window.ActualHeight, window.IsActive,
            dpi.DpiScaleX, dpi.DpiScaleY,
            monitors = System.Windows.Forms.Screen.AllScreens.Select(screen => new { screen.Bounds, screen.WorkingArea })
        }, JsonOptions));
        if (!verified) Console.WriteLine($"Native screen unverified ({reason}): {Path.GetFileName(output)}");
        return new(new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height), verified);
    }

    private static string VerifyDesktopPixels(IntPtr handle, Rectangle requested, Rectangle bounds, string output)
    {
        using var owned = new Bitmap(requested.Width, requested.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(owned))
        {
            graphics.Clear(System.Drawing.Color.Black);
            var dc = graphics.GetHdc();
            try
            {
                // PW_CLIENTONLY maps to the same client origin as Window.PointToScreen.
                if (!PrintFixtureWindow(handle, dc, 1)) return "hwnd-render-unavailable";
            }
            finally { graphics.ReleaseHdc(dc); }
        }
        using var physical = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(physical))
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        var part = new Rectangle(bounds.X - requested.X, bounds.Y - requested.Y, bounds.Width, bounds.Height);
        using var visible = owned.Clone(part, PixelFormat.Format24bppRgb);
        var verification = CompareFixturePixels(visible, physical);
        if (verification == "verified") visible.Save(output, ImageFormat.Png);
        return verification;
    }

    private static string CompareFixturePixels(Bitmap owned, Bitmap physical)
    {
        var area = new Rectangle(0, 0, owned.Width, owned.Height);
        var a = owned.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var b = physical.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var expected = new byte[checked(owned.Width * 3)];
                var actual = new byte[expected.Length];
                (byte B, byte G, byte R) first = default;
                var varied = false;
                for (var y = 0; y < owned.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(a.Scan0, y * a.Stride), expected, 0, expected.Length);
                    Marshal.Copy(IntPtr.Add(b.Scan0, y * b.Stride), actual, 0, actual.Length);
                    if (!expected.AsSpan().SequenceEqual(actual)) return "desktop-hwnd-mismatch";
                    if (y == 0) first = (expected[0], expected[1], expected[2]);
                    for (var x = 0; x < expected.Length && !varied; x += 3)
                        varied = (expected[x], expected[x + 1], expected[x + 2]) != first;
                }
                return varied ? "verified" : "uniform-hwnd-render";
            }
            finally { physical.UnlockBits(b); }
        }
        finally { owned.UnlockBits(a); }
    }

    private static void VerifyDesktopComparison()
    {
        using var owned = new Bitmap(5, 3, PixelFormat.Format24bppRgb);
        using var physical = new Bitmap(5, 3, PixelFormat.Format24bppRgb);
        Require(CompareFixturePixels(owned, physical) == "uniform-hwnd-render", "A blank HWND counted as evidence.");
        owned.SetPixel(4, 2, System.Drawing.Color.Red);
        Require(CompareFixturePixels(owned, physical) == "desktop-hwnd-mismatch", "An occluded pixel counted as evidence.");
        physical.SetPixel(4, 2, System.Drawing.Color.Red);
        Require(CompareFixturePixels(owned, physical) == "verified", "Matching nonuniform RGB pixels were rejected.");
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "PrintWindow", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintFixtureWindow(IntPtr window, IntPtr dc, uint flags);
}
