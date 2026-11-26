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
    private readonly record struct ClientAttempt(uint Flags, string Status, int DifferentPixels, bool HasDrawing,
        PixelDifferences? Differences = null, int? OwnedBefore = null, int? OwnedAfter = null);
    private readonly record struct DesktopCapture(Rect VisibleWindowBounds, Rect? VerifiedClientBounds)
    {
        internal static DesktopCapture From(Rectangle windowBounds, Rectangle? comparedClient)
        {
            static Rect Pixels(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
            return new(Pixels(windowBounds), comparedClient is { } client ? Pixels(client) : null);
        }
    }

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
            File.Delete(Path.ChangeExtension(file, "hwnd.png"));
        }
    }

    // An active WPF window may still be covered by setup or a different desktop.
    // Save only owner-rendered pixels after a physical-screen comparison. The
    // separate WPF .png and geometry remain available if this check cannot run.
    private static DesktopCapture CaptureScreen(Window window, string output)
    {
        File.Delete(output); // A previous run must not survive an unverified attempt.
        File.Delete(output + ".capture.json");
        var ownedOutput = Path.ChangeExtension(output, "hwnd.png");
        File.Delete(ownedOutput);
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
        Rectangle? nativeClient = null;
        var attempts = new List<ClientAttempt>();
        var ownedSamplePoints = 0;
        if (handle != IntPtr.Zero && WindowProcess(handle, out var owner) != 0 && owner == (uint)Environment.ProcessId)
        {
            try
            {
                var topLeft = new PointerPoint();
                if (CaptureClientRect(handle, out var client) && CaptureClientOrigin(handle, ref topLeft))
                {
                    var clientArea = new Rectangle(topLeft.X, topLeft.Y, client.Right - client.Left, client.Bottom - client.Top);
                    nativeClient = clientArea;
                    var visibleClient = Rectangle.Intersect(clientArea, virtualScreen);
                    if (visibleClient.Width > 0 && visibleClient.Height > 0)
                    {
                        ownedSamplePoints = OwnedClientSamples(handle, visibleClient);
                        // Use the OS client origin for PW_CLIENTONLY. WPF Window.ActualWidth
                        // can include non-client chrome, particularly at fractional DPI.
                        var flags = new uint[] { 3, 1 }; // PW_CLIENTONLY | PW_RENDERFULLCONTENT, then PW_CLIENTONLY.
                        foreach (uint flag in flags)
                        {
                            var attempt = VerifyDesktopPixels(handle, clientArea, visibleClient, output, ownedOutput, flag);
                            attempts.Add(attempt);
                            if (attempt.OwnedBefore is int sampled) ownedSamplePoints = sampled;
                            reason = attempt.Status;
                            if (reason == "verified") break;
                        }
                    }
                    else reason = "client-offscreen";
                }
                else reason = "client-geometry-unavailable";
            }
            catch (System.ComponentModel.Win32Exception) { reason = "desktop-read-unavailable"; }
            catch (ExternalException) { reason = "native-render-unavailable"; }
        }
        var verified = reason == "verified";
        Rectangle? captured = verified && nativeClient.HasValue ? Rectangle.Intersect(nativeClient.Value, virtualScreen) : null;
        if (!verified) File.Delete(output);
        File.WriteAllText(output + ".capture.json", JsonSerializer.Serialize(new {
            captureStatus = reason, screenPixelsVerified = verified, method = "PrintWindow client / desktop exact RGB",
            requested, nativeClient, virtualScreen, visibleBounds = bounds,
            ownedSamplePoints, samplePoints = 9, attempts,
            captured,
            fullWindowCaptured = verified && nativeClient.HasValue && requested == nativeClient.Value && requested == bounds,
            window.Left, window.Top, window.ActualWidth, window.ActualHeight, window.IsActive,
            dpi.DpiScaleX, dpi.DpiScaleY,
            monitors = System.Windows.Forms.Screen.AllScreens.Select(screen => new { screen.Bounds, screen.WorkingArea })
        }, JsonOptions));
        if (!verified) Console.WriteLine($"Native screen unverified ({reason}): {Path.GetFileName(output)}");
        return DesktopCapture.From(bounds, captured);
    }

    private static ClientAttempt VerifyDesktopPixels(IntPtr handle, Rectangle requested, Rectangle bounds,
        string output, string ownedOutput, uint flags)
    {
        using var owned = new Bitmap(requested.Width, requested.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(owned))
        {
            graphics.Clear(System.Drawing.Color.Black);
            var dc = graphics.GetHdc();
            try
            {
                // Both modes paint from the client origin returned by ClientToScreen.
                if (!PrintFixtureWindow(handle, dc, flags)) return new(flags, "hwnd-render-unavailable", -1, false);
            }
            finally { graphics.ReleaseHdc(dc); }
        }
        var part = new Rectangle(bounds.X - requested.X, bounds.Y - requested.Y, bounds.Width, bounds.Height);
        using var visible = owned.Clone(part, PixelFormat.Format24bppRgb);
        var before = OwnedClientSamples(handle, bounds);
        ClientAttempt comparison;
        if (before == 9 && EntireClientOwned(handle, bounds))
        {
            using var physical = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(physical))
                graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            var after = OwnedClientSamples(handle, bounds);
            if (after == 9 && EntireClientOwned(handle, bounds))
            {
                var cursor = System.Windows.Forms.Cursor.Position;
                var differences = new PixelDifferences(requested, bounds, cursor);
                comparison = CompareFixturePixels(visible, physical, flags, differences);
                differences.CheckOwnership(handle);
            }
            else comparison = OwnershipFailure(visible, flags);
            comparison = comparison with { OwnedBefore = before, OwnedAfter = after };
        }
        else comparison = OwnershipFailure(visible, flags) with { OwnedBefore = before };
        // Save only the bitmap Windows drew from our process-owned HWND.
        if (comparison.HasDrawing && (!File.Exists(ownedOutput) || comparison.Status == "verified"))
            visible.Save(ownedOutput, ImageFormat.Png);
        if (comparison.Status == "verified") visible.Save(output, ImageFormat.Png);
        return comparison;
    }

    private static bool EntireClientOwned(IntPtr handle, Rectangle bounds)
    {
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        for (int x = bounds.Left; x < bounds.Right; x++)
            if (!IsFixtureAt(handle, x, y)) return false;
        return true;
    }

    private static ClientAttempt OwnershipFailure(Bitmap owned, uint flags) =>
        CompareFixturePixels(owned, owned, flags, sameBitmap: true) with { Status = "fixture-ownership-incomplete", DifferentPixels = -1 };

    private static ClientAttempt CompareFixturePixels(Bitmap owned, Bitmap physical, uint flags = 0,
        PixelDifferences? differences = null, bool sameBitmap = false)
    {
        var area = new Rectangle(0, 0, owned.Width, owned.Height);
        var a = owned.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var b = sameBitmap ? a : physical.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var expected = new byte[checked(owned.Width * 3)];
                var actual = new byte[expected.Length];
                (byte B, byte G, byte R) first = default;
                var varied = false;
                var different = 0;
                for (var y = 0; y < owned.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(a.Scan0, y * a.Stride), expected, 0, expected.Length);
                    if (!sameBitmap) Marshal.Copy(IntPtr.Add(b.Scan0, y * b.Stride), actual, 0, actual.Length);
                    if (!sameBitmap && !expected.AsSpan().SequenceEqual(actual))
                        for (var offset = 0; offset < expected.Length; offset += 3)
                            if (expected[offset] != actual[offset] || expected[offset + 1] != actual[offset + 1] ||
                                expected[offset + 2] != actual[offset + 2])
                            {
                                different++;
                                differences?.Observe(offset / 3, y, Math.Abs(expected[offset + 2] - actual[offset + 2]),
                                    Math.Abs(expected[offset + 1] - actual[offset + 1]), Math.Abs(expected[offset] - actual[offset]));
                            }
                    if (y == 0) first = (expected[0], expected[1], expected[2]);
                    for (var x = 0; x < expected.Length && !varied; x += 3)
                        varied = (expected[x], expected[x + 1], expected[x + 2]) != first;
                }
                return new(flags, different > 0 ? "desktop-hwnd-mismatch" : varied ? "verified" : "uniform-hwnd-render", different, varied, differences);
            }
            finally { if (!sameBitmap) physical.UnlockBits(b); }
        }
        finally { owned.UnlockBits(a); }
    }

    private static void VerifyDesktopComparison()
    {
        var window = new Rectangle(-12, 5, 120, 90);
        var verified = new Rectangle(0, 9, 104, 80);
        var capture = DesktopCapture.From(window, verified);
        Require(capture.VisibleWindowBounds == new Rect(-12, 5, 120, 90) &&
                capture.VerifiedClientBounds == new Rect(0, 9, 104, 80) &&
                DesktopCapture.From(window, null).VerifiedClientBounds is null,
            "Verified native client and visible layout bounds must remain distinct.");
        using var owned = new Bitmap(5, 3, PixelFormat.Format24bppRgb);
        using var physical = new Bitmap(5, 3, PixelFormat.Format24bppRgb);
        Require(CompareFixturePixels(owned, physical) == new ClientAttempt(0, "uniform-hwnd-render", 0, false), "A blank HWND counted as evidence.");
        owned.SetPixel(4, 2, System.Drawing.Color.Red);
        Require(CompareFixturePixels(owned, physical) == new ClientAttempt(0, "desktop-hwnd-mismatch", 1, true), "An occluded pixel counted as evidence.");
        physical.SetPixel(0, 1, System.Drawing.Color.Lime);
        Require(CompareFixturePixels(owned, physical, 3) == new ClientAttempt(3, "desktop-hwnd-mismatch", 2, true),
            "Pixel differences across scanlines were counted incorrectly.");
        var observed = new PixelDifferences(new Rectangle(-6, -3, 80, 50), new Rectangle(0, 0, 5, 3),
            new System.Drawing.Point(0, 1));
        var diagnostic = CompareFixturePixels(owned, physical, 3, observed);
        Require(diagnostic.DifferentPixels == 2 && observed.ClientPixelBounds == new Rectangle(6, 4, 5, 2) &&
                observed.TopLeft16 == 2 && observed.WithinEightPixelsOfEdge == 2 && observed.Within32OfCursorHotspot == 2 &&
                observed.MaxRedDelta == 255 && observed.MaxGreenDelta == 255 && observed.MaxBlueDelta == 0 &&
                observed.ChangedPointsOwned is null, "Difference geometry or aggregate RGB delta was lost.");
        Require(OwnershipFailure(owned, 1) == new ClientAttempt(1, "fixture-ownership-incomplete", -1, true),
            "An unexamined physical screen must remain unverified.");
        physical.SetPixel(0, 1, System.Drawing.Color.Black);
        physical.SetPixel(4, 2, System.Drawing.Color.Red);
        Require(CompareFixturePixels(owned, physical) == new ClientAttempt(0, "verified", 0, true), "Matching nonuniform RGB pixels were rejected.");
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetClientRect", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CaptureClientRect(IntPtr window, out PointerRectangle rect);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "ClientToScreen", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CaptureClientOrigin(IntPtr window, ref PointerPoint point);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "PrintWindow", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintFixtureWindow(IntPtr window, IntPtr dc, uint flags);
}
