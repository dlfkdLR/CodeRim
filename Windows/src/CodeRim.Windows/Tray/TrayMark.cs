using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodeRim.Windows.Tray;

/// <summary>The same two ring segments as macOS CodeRimMark, without an application-icon background.</summary>
internal static class TrayMark
{
    internal static Color Foreground()
    {
        if (SystemInformation.HighContrast) return SystemColors.WindowText;
        // The taskbar theme is independent of AppsUseLightTheme (the settings window).
        try
        {
            var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0);
            return light is int value && value != 0 ? Color.FromArgb(38, 38, 38) : Color.FromArgb(230, 230, 230);
        }
        catch (Exception e) when (e is System.Security.SecurityException or System.IO.IOException or UnauthorizedAccessException)
        { return SystemColors.WindowText; }
    }
    internal static Bitmap Render(int size, Color color)
    {
        size = Math.Clamp(size, 16, 256);
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = size / 18f; var diameter = size - inset * 2; var inner = diameter * .34f;
        var outerBounds = new RectangleF(inset, inset, diameter, diameter);
        var innerBounds = new RectangleF(size / 2f - inner, size / 2f - inner, inner * 2, inner * 2);
        using var brush = new SolidBrush(color);
        foreach (var (start, sweep) in new[] { (45f, 270f), (-24f, 48f) })
        {
            using var path = new GraphicsPath();
            path.AddArc(outerBounds, start, sweep); path.AddArc(innerBounds, start + sweep, -sweep); path.CloseFigure();
            graphics.FillPath(brush, path);
        }
        return bitmap;
    }
    internal static Icon Create(int size, Color color)
    {
        using var bitmap = Render(size, color);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
