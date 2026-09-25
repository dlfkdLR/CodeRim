using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task CaptureOffsetRegression(string directory)
    {
        var canvas = new Canvas();
        var panel = new Grid { Width = 120, Height = 80, Background = Brushes.Red };
        panel.Children.Add(new Border { Width = 25, Background = Brushes.Blue, HorizontalAlignment = HorizontalAlignment.Right });
        canvas.Children.Add(panel);
        var window = new Window { Content = canvas, Width = 270, Height = 220, Title = "Capture coordinates fixture" };
        Exception? failure = null; Exception? cleanup = null;
        try
        {
            window.Show(); await Idle();
            foreach (var offset in new[] { new Point(0, 0), new Point(36, 28) })
            {
                Canvas.SetLeft(panel, offset.X); Canvas.SetTop(panel, offset.Y); panel.UpdateLayout(); await Idle();
                var path = Path.Combine(directory, offset.X == 0 ? "windows-capture-origin.png" : "windows-capture-offset.png");
                Capture(panel, path);
                using var file = File.OpenRead(path);
                var frame = new PngBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                Require(frame.PixelWidth == 120 && frame.PixelHeight == 80, "Capture changed the mounted child's dimensions");
                var pixels = new byte[120 * 80 * 4];
                new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 120 * 4, 0);
                static bool ColorAt(byte[] values, int x, int y, byte red, byte blue)
                {
                    var index = (y * 120 + x) * 4;
                    return values[index] == blue && values[index + 1] == 0 && values[index + 2] == red && values[index + 3] == 255;
                }
                Require(ColorAt(pixels, 5, 5, 255, 0) && ColorAt(pixels, 115, 5, 0, 255)
                    && ColorAt(pixels, 5, 75, 255, 0) && ColorAt(pixels, 115, 75, 0, 255), "Capture shifted or clipped the mounted child's colored edges");
            }
            File.WriteAllText(Path.Combine(directory, "windows-capture-coordinates.json"), System.Text.Json.JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Child capture dimensions", "Exact four-corner pixels at zero and nonzero X/Y layout offsets" } }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { window.Close(); }
            catch (Exception error) { cleanup = error; }
        }
        if (failure is not null && cleanup is not null) throw new AggregateException("Capture fixture and cleanup failed", failure, cleanup);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanup is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
    }
}
