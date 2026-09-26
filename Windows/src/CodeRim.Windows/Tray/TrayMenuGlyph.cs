using System.Drawing;
using System.Drawing.Drawing2D;

namespace CodeRim.Windows.Tray;

internal enum TrayMenuSymbol { Settings, Quit }

internal static class TrayMenuGlyph
{
    internal static Bitmap Render(TrayMenuSymbol symbol)
    {
        var image = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(image);
        Draw(graphics, new Rectangle(0, 0, 16, 16), symbol, Color.Black);
        return image;
    }

    // Paint vectors at the actual menu DPI and foreground, including selection
    // and high contrast. The item image reserves the native image-column space.
    internal static void Draw(Graphics graphics, Rectangle bounds, TrayMenuSymbol symbol, Color color)
    {
        var state = graphics.Save();
        try
        {
            graphics.TranslateTransform(bounds.Left, bounds.Top);
            graphics.ScaleTransform(bounds.Width / 16f, bounds.Height / 16f);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            if (symbol == TrayMenuSymbol.Settings)
            {
                var points = new PointF[32];
                for (var tooth = 0; tooth < 8; tooth++)
                    for (var edge = 0; edge < 4; edge++)
                    {
                        var offset = edge switch { 0 => -14, 1 => -9, 2 => 9, _ => 14 };
                        var angle = (tooth * 45 + offset - 90) * MathF.PI / 180;
                        var radius = edge is 1 or 2 ? 6.8f : 5.5f;
                        points[tooth * 4 + edge] = new PointF(8 + radius * MathF.Cos(angle), 8 + radius * MathF.Sin(angle));
                    }
                graphics.DrawPolygon(pen, points);
                graphics.DrawEllipse(pen, 5.5f, 5.5f, 5, 5);
            }
            else
            {
                using var frame = TrayMenuRenderer.Rounded(new RectangleF(1, 3, 14, 10), 1.7f);
                graphics.DrawPath(pen, frame);
                graphics.DrawLine(pen, 5.5f, 5.5f, 10.5f, 10.5f);
                graphics.DrawLine(pen, 10.5f, 5.5f, 5.5f, 10.5f);
            }
        }
        finally { graphics.Restore(state); }
    }
}
