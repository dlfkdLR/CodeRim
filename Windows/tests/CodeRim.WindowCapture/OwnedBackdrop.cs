using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace CodeRim.WindowCapture;

// CI-only. Never retain physical pixels. All source pixels are composed on the
// test's own opaque background and every sampled screen point must belong to one of these two windows.
internal static class OwnedBackdrop
{
    private static readonly Color BackColor = Color.FromArgb(31, 101, 179);
    private static readonly WindowProc Procedure = DefWindowProcW;

    internal static object Compare(nint source, int pid, Rectangle client, byte[] frame, bool straightAlpha)
    {
        var top = Samples(source, 0, client);
        if (top != 9) return new { status = "fixture-not-visible", initiallyOwned = top,
            fixtureVisible = IsWindowVisible(source), fixtureMinimized = IsIconic(source),
            fixtureForeground = GetForegroundWindow() == source, interactive = Environment.UserInteractive,
            remoteSession = System.Windows.Forms.SystemInformation.TerminalServerSession };
        nint instance = GetModuleHandleW(null);
        nint brush = CreateSolidBrush((uint)(BackColor.R | BackColor.G << 8 | BackColor.B << 16));
        if (brush == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var windowClass = new WindowClass
        {
            procedure = Marshal.GetFunctionPointerForDelegate(Procedure), instance = instance, brush = brush,
            name = "CodeRim.CompositorFixtureBackdrop"
        };
        ushort atom = RegisterClassW(ref windowClass);
        nint backing = 0;
        try
        {
            if (atom == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var area = Rectangle.Inflate(client, 16, 16);
            backing = CreateWindowExW(0x08000080 /* NOACTIVATE | TOOLWINDOW */, windowClass.name, "", 0x80000000 /* POPUP */,
                area.X, area.Y, area.Width, area.Height, 0, 0, instance, 0);
            if (backing == 0 || !NativeCapture.Owned(backing, Environment.ProcessId))
                throw new InvalidOperationException("Unable to own the fixture background.");
            if (!SetWindowPos(backing, source, area.X, area.Y, area.Width, area.Height, 0x50 /* SHOWWINDOW | NOACTIVATE */))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            UpdateWindow(backing);
            Marshal.ThrowExceptionForHR(DwmFlush());
            var before = Ownership(source, backing, client);
            if (!before.Complete || !NativeCapture.Owned(source, pid))
                return new { status = "controlled-ownership-incomplete-before", before };
            using var physical = new Bitmap(client.Width, client.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(physical))
                g.CopyFromScreen(client.Location, Point.Empty, client.Size, CopyPixelOperation.SourceCopy);
            var after = Ownership(source, backing, client);
            if (!after.Complete || !NativeCapture.Owned(source, pid))
                return new { status = "controlled-ownership-incomplete-after", before, after };
            int different = 0, transparent = 0, partial = 0;
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var surface = physical.LockBits(new Rectangle(Point.Empty, client.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[client.Width * 4];
                var bgr = new byte[client.Width * 3];
                for (int y = 0; y < client.Height; y++)
                {
                    Marshal.Copy(surface.Scan0 + y * surface.Stride, row, 0, row.Length);
                    for (int i = 0; i < row.Length; i += 4)
                    {
                        int k = y * row.Length + i, alpha = frame[k + 3];
                        int dest = i / 4 * 3;
                        bgr[dest] = row[i]; bgr[dest + 1] = row[i + 1]; bgr[dest + 2] = row[i + 2];
                        if (alpha == 0) transparent++;
                        else if (alpha < 255) partial++;
                        if (Blend(frame[k], BackColor.B, alpha, straightAlpha) != row[i] ||
                            Blend(frame[k + 1], BackColor.G, alpha, straightAlpha) != row[i + 1] ||
                            Blend(frame[k + 2], BackColor.R, alpha, straightAlpha) != row[i + 2]) different++;
                    }
                    digest.AppendData(bgr);
                }
            }
            finally { physical.UnlockBits(surface); }
            return new { status = "controlled-frame-compared", before, after, different, transparent, partial,
                total = frame.Length / 4, backingRgb = new[] { BackColor.R, BackColor.G, BackColor.B }.Select(v => (int)v),
                exact = different == 0, straightAlpha,
                physicalBgrSha256 = Convert.ToHexStringLower(digest.GetHashAndReset()) };
        }
        finally
        {
            if (backing != 0) DestroyWindow(backing);
            if (atom != 0) UnregisterClassW(windowClass.name, instance);
            DeleteObject(brush);
        }
    }

    private static int Blend(int foreground, int background, int alpha, bool straight) => straight
        ? (foreground * alpha + background * (255 - alpha) + 127) / 255
        : Math.Min(255, foreground + (background * (255 - alpha) + 127) / 255);

    private static int Samples(nint target, nint backing, Rectangle area)
    {
        int count = 0;
        for (int i = 1; i <= 5; i += 2)
        for (int j = 1; j <= 5; j += 2)
            if (OwnerAt(target, backing, new(area.X + area.Width * i / 6, area.Y + area.Height * j / 6)) != 0) count++;
        return count;
    }

    private readonly record struct Coverage(int Target, int Backing, bool Complete);
    private static Coverage Ownership(nint source, nint backing, Rectangle area)
    {
        int targetCount = 0, backingCount = 0;
        for (int y = area.Top; y < area.Bottom; y++)
        for (int x = area.Left; x < area.Right; x++)
        {
            int owner = OwnerAt(source, backing, new(x, y));
            if (owner == 0) return new(targetCount, backingCount, false);
            if (owner == 1) targetCount++; else backingCount++;
        }
        return new(targetCount, backingCount, true);
    }

    private static int OwnerAt(nint source, nint backing, Point point)
    {
        nint hwnd = WindowFromPoint(point);
        if (hwnd == 0) return 0;
        if (hwnd != source && hwnd != backing) hwnd = GetAncestor(hwnd, 2);
        return hwnd == source ? 1 : hwnd != 0 && hwnd == backing ? 2 : 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint style;
        internal nint procedure;
        internal int classExtra, windowExtra;
        internal nint instance, icon, cursor, brush;
        internal string? menu;
        internal string name;
    }

    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint DefWindowProcW(nint w, uint msg, nint wp, nint lp);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern ushort RegisterClassW(ref WindowClass cls);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, nint instance);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint target, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flag);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
}
