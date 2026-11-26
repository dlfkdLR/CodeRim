using System.Drawing;
using System.Runtime.InteropServices;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static bool IsFixtureAt(IntPtr handle, int x, int y)
    {
        var hit = WindowAtPoint(new PointerPoint { X = x, Y = y });
        return hit != IntPtr.Zero && CaptureRoot(hit, 2 /* GA_ROOT */) == handle &&
            WindowProcess(hit, out var pid) != 0 && pid == (uint)Environment.ProcessId;
    }

    private static int OwnedClientSamples(IntPtr handle, Rectangle bounds)
    {
        int count = 0;
        for (int i = 1; i <= 5; i += 2)
        for (int j = 1; j <= 5; j += 2)
            if (IsFixtureAt(handle, bounds.Left + i * bounds.Width / 6, bounds.Top + j * bounds.Height / 6)) count++;
        return count;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetAncestor", ExactSpelling = true)]
    private static extern IntPtr CaptureRoot(IntPtr window, uint flags);

    // Only aggregate magnitudes and client-local geometry leave this comparison.
    private sealed class PixelDifferences(Rectangle client, Rectangle sampled, Point cursor)
    {
        private readonly List<Point> changed = new(256);
        private int count;
        public Rectangle? ClientPixelBounds { get; private set; }
        public int WithinEightPixelsOfEdge { get; private set; }
        public int TopLeft16 { get; private set; }
        public int TopRight16 { get; private set; }
        public int BottomLeft16 { get; private set; }
        public int BottomRight16 { get; private set; }
        public int Within32OfCursorHotspot { get; private set; }
        public int MaxRedDelta { get; private set; }
        public int MaxGreenDelta { get; private set; }
        public int MaxBlueDelta { get; private set; }
        public int MaxAbsoluteDeltaOne { get; private set; }
        public int? ChangedPointsOwned { get; private set; }

        internal void Observe(int x, int y, int red, int green, int blue)
        {
            int screenX = sampled.X + x, screenY = sampled.Y + y;
            int left = screenX - client.Left, top = screenY - client.Top;
            int right = client.Width - 1 - left, bottom = client.Height - 1 - top;
            var point = new Rectangle(left, top, 1, 1);
            ClientPixelBounds = ClientPixelBounds is { } prior ? Rectangle.Union(prior, point) : point;
            if (Math.Min(Math.Min(left, right), Math.Min(top, bottom)) < 8) WithinEightPixelsOfEdge++;
            if (left < 16 && top < 16) TopLeft16++;
            if (right < 16 && top < 16) TopRight16++;
            if (left < 16 && bottom < 16) BottomLeft16++;
            if (right < 16 && bottom < 16) BottomRight16++;
            if (Math.Abs(screenX - cursor.X) <= 32 && Math.Abs(screenY - cursor.Y) <= 32)
                Within32OfCursorHotspot++;
            MaxRedDelta = Math.Max(red, MaxRedDelta);
            MaxGreenDelta = Math.Max(green, MaxGreenDelta);
            MaxBlueDelta = Math.Max(blue, MaxBlueDelta);
            if (Math.Max(red, Math.Max(green, blue)) == 1) MaxAbsoluteDeltaOne++;
            if (++count <= 256) changed.Add(new(screenX, screenY));
        }

        internal void CheckOwnership(IntPtr handle)
        {
            // Resolve every changed pixel of a small discrepancy without unbounded UI calls.
            if (count <= 256) ChangedPointsOwned = changed.Count(p => IsFixtureAt(handle, p.X, p.Y));
        }
    }
}
