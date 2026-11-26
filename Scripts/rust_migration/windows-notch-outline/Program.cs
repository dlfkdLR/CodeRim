using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodeRim.Core.Domain;
using CodeRim.Windows.Views;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length > 0) { Measure(args[0]); return; }

        using var vectors = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var pixels = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var paths = 0;
        var images = 0;
        var cached = 0;
        foreach (var edge in Enum.GetValues<NotchEdge>())
        foreach (var scale in new[] { .5, .75, 1, 1.25, 1.5, 2, 3 })
        {
            var shape = new NotchShape { Edge = edge, DesignScale = scale };
            foreach (var size in new[] { new Size(0, 0), new Size(.25, .5), new Size(7, 31),
                new Size(40, 89), new Size(68.5, 121.75), new Size(160, 39) })
            foreach (var progress in new[] { -2, 0, double.Epsilon, .01, .125, .5, .8, Math.BitDecrement(1d), 1, 5 })
            {
                shape.Expansion = progress;
                var path = shape.GeometryFor(size);
                var again = shape.GeometryFor(size);
                if (!again.IsFrozen || !path.IsFrozen) throw new InvalidDataException("Mutable geometry");
                if (ReferenceEquals(path, again) && !ReferenceEquals(path, Geometry.Empty)) cached++;
                var record = Encoding.UTF8.GetBytes(path.ToString(CultureInfo.InvariantCulture) + "\n");
                vectors.AppendData(record);
                paths++;
                if (scale is 1 or 1.5 && size.Width >= 7 && progress is 0 or .125 or .5 or 1)
                {
                    var image = new RenderTargetBitmap(180, 160, 96, 96, PixelFormats.Pbgra32);
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen()) drawing.DrawGeometry(Brushes.Black, null, path);
                    image.Render(visual);
                    var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
                    image.CopyPixels(bytes, image.PixelWidth * 4, 0);
                    if (!bytes.Any(value => value != 0)) throw new InvalidDataException("Blank notch render");
                    pixels.AppendData(bytes);
                    images++;
                }
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { paths, images, cached,
            vectors = Convert.ToHexString(vectors.GetHashAndReset()), pixels = Convert.ToHexString(pixels.GetHashAndReset()) }));
    }

    private static void Measure(string mode)
    {
        var shape = new NotchShape { Edge = NotchEdge.Left, DesignScale = 1 };
        var size = new Size(69, 242);
        var changes = mode == "animation";
        if (!changes && mode != "steady") throw new ArgumentException("Unknown workload");
        for (int i = 0; i < 256; i++) { shape.Expansion = (i % 61) / 60d; _ = shape.GeometryFor(size); }
        Geometry? last = null;
        var startBytes = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 4096; i++)
        {
            if (changes) shape.Expansion = (i % 61) / 60d;
            last = shape.GeometryFor(size);
            // Existing window requests the same frame for the clip and the painted Shape.
            last = shape.GeometryFor(size);
        }
        var elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
        GC.KeepAlive(last);
        Console.WriteLine(JsonSerializer.Serialize(new { ns = elapsed / 4096, bytes = bytes / 4096.0 }));
    }
}
