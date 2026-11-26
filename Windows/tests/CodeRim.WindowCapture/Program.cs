using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace CodeRim.WindowCapture;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || args.Length != 2 ||
            !int.TryParse(args[0], out int pid)) return 2;
        NativeCapture.SetProcessDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2, before reading geometry.
        string directory = Path.GetFullPath(args[1]), prefix = Path.Combine(directory, "account-compositor");
        using var process = Process.GetProcessById(pid);
        _ = process.SafeHandle; // Keep the installed fixture's process identity alive for the entire rendezvous.
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CodeRim", "CodeRim.exe");
        if (!string.Equals(process.MainModule?.FileName, installed, StringComparison.OrdinalIgnoreCase)) return 3;
        object result;
        try
        {
            var limit = Stopwatch.StartNew();
            while (!File.Exists(prefix + ".ready") && !process.HasExited && limit.Elapsed < TimeSpan.FromSeconds(155))
                await Task.Delay(50);
            if (!File.Exists(prefix + ".ready"))
                result = new { status = "fixture-not-reached", processExited = process.HasExited };
            else
            {
                using var offer = JsonDocument.Parse(File.ReadAllBytes(prefix + ".ready"));
                nint hwnd = (nint)offer.RootElement.GetProperty("hwnd").GetInt64();
                if (offer.RootElement.GetProperty("pid").GetInt32() != pid || !NativeCapture.Owned(hwnd, pid)) return 4;
                result = await Probe(hwnd, process, directory, prefix);
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or NotSupportedException or TimeoutException)
        {
            result = new { status = "compositor-unavailable", error = ex.GetType().Name, hr = ex.HResult };
        }
        finally { File.WriteAllText(prefix + ".done", ""); }
        File.WriteAllText(prefix + ".json", JsonSerializer.Serialize(result));
        return 0;
    }

    private static async Task<object> Probe(nint hwnd, Process process, string directory, string prefix)
    {
        if (!GraphicsCaptureSession.IsSupported()) return new { status = "compositor-unsupported" };
        var geometry = NativeCapture.Bounds(hwnd);
        var item = NativeCapture.Item(hwnd);
        using var device = NativeCapture.Device();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        var available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (_, _) => available.TrySetResult();
        session.StartCapture();
        await available.Task.WaitAsync(TimeSpan.FromSeconds(4));
        using var frame = pool.TryGetNextFrame();
        if (frame is null) return new { status = "compositor-no-frame" };
        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
        if (process.HasExited || !NativeCapture.Owned(hwnd, process.Id) || NativeCapture.Bounds(hwnd) != geometry ||
            !File.Exists(prefix + ".ready")) return new { status = "compositor-window-changed" };
        int width = frame.ContentSize.Width, height = frame.ContentSize.Height;
        if (bitmap.PixelWidth != width || bitmap.PixelHeight != height || bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            return new { status = "compositor-surface-size-or-format-changed", width, height };
        var candidates = new[] { geometry.Window, geometry.Extended }.Distinct()
            .Where(r => r.Width == width && r.Height == height).ToArray();
        var client = Rectangle.Intersect(geometry.Client, System.Windows.Forms.SystemInformation.VirtualScreen);
        if (candidates.Length != 1 || !candidates[0].Contains(client) || client.Width <= 0 || client.Height <= 0)
            return new { status = "compositor-unmapped-frame", width, height, client, geometry.Window, geometry.Extended };
        var data = new byte[checked(width * height * 4)];
        var buffer = new global::Windows.Storage.Streams.Buffer((uint)data.Length);
        bitmap.CopyToBuffer(buffer);
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(data);
        int left = client.Left - candidates[0].Left, top = client.Top - candidates[0].Top;
        var pixels = new byte[checked(client.Width * client.Height * 4)];
        for (int y = 0; y < client.Height; y++)
            Array.Copy(data, ((y + top) * width + left) * 4, pixels, y * client.Width * 4, client.Width * 4);
        string ownedPath = Path.Combine(directory, "windows-account-history-overview.screen.hwnd.png");
        return Compare(pixels, client.Size, ownedPath, width, height);
    }

    private static object Compare(byte[] pixels, Size size, string ownedPath, int frameWidth, int frameHeight)
    {
        int nonOpaque = 0;
        bool varied = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] != 255) nonOpaque++;
            if (!pixels.AsSpan(i, 3).SequenceEqual(pixels.AsSpan(0, 3))) varied = true;
        }
        int? mismatches = null;
        if (File.Exists(ownedPath))
        {
            using var png = new Bitmap(ownedPath);
            if (png.Size == size)
            {
                var area = new Rectangle(Point.Empty, size);
                var locked = png.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[size.Width * 4];
                    int different = 0;
                    for (int y = 0; y < size.Height; y++)
                    {
                        Marshal.Copy(locked.Scan0 + y * locked.Stride, row, 0, row.Length);
                        for (int i = 0; i < row.Length; i += 4)
                            if (!row.AsSpan(i, 3).SequenceEqual(pixels.AsSpan(y * row.Length + i, 3))) different++;
                    }
                    mismatches = different;
                }
                finally { png.UnlockBits(locked); }
            }
        }
        return new { status = "compositor-owned-frame", method = "GraphicsCaptureItem.CreateForWindow",
            frameWidth, frameHeight, width = size.Width, height = size.Height, nonOpaque, varied, differentFromOwnedPrintWindow = mismatches,
            exactOpaqueOwnedMatch = mismatches == 0 && varied && nonOpaque == 0,
            bgraSha256 = Convert.ToHexStringLower(SHA256.HashData(pixels)) };
    }
}
