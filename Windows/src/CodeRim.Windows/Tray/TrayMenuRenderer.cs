using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CodeRim.Windows.Views;

namespace CodeRim.Windows.Tray;

internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    private static bool Contrast => SystemInformation.HighContrast;
    private static Color Background => Contrast ? SystemColors.Menu : SettingsTheme.IsDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(246, 246, 246);
    private static Color Foreground => Contrast ? SystemColors.MenuText : SettingsTheme.IsDark ? Color.FromArgb(230, 230, 230) : Color.FromArgb(38, 38, 38);
    private static Color Separator => Contrast ? SystemColors.MenuText : SettingsTheme.IsDark ? Color.FromArgb(67, 67, 67) : Color.FromArgb(211, 211, 211);
    private static float Scale(ToolStrip? strip) => (strip?.DeviceDpi ?? 96) / 96f;
    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath(); var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
    }
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(Background);
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Separator);
        using var path = Rounded(new RectangleF(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 9 * Scale(e.ToolStrip));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.DrawPath(pen, path);
    }
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var scale = Scale(e.ToolStrip); var bounds = new RectangleF(4 * scale, 0, e.Item.Width - 8 * scale, e.Item.Height);
        using var path = Rounded(bounds, 5 * scale);
        using var brush = new SolidBrush(Contrast ? SystemColors.Highlight : Color.FromArgb(30, 88, 190));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.FillPath(brush, path);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = ItemColor(e.Item);
        base.OnRenderItemText(e);
    }
    private static Color ItemColor(ToolStripItem item) => !item.Enabled ? SystemColors.GrayText
        : item.Selected ? (Contrast ? SystemColors.HighlightText : Color.White) : Foreground;
    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Item.Tag is TrayMenuSymbol symbol) TrayMenuGlyph.Draw(e.Graphics, e.ImageRectangle, symbol, ItemColor(e.Item));
        else base.OnRenderItemImage(e);
    }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var scale = Scale(e.ToolStrip); var center = new PointF(e.ImageRectangle.Left + e.ImageRectangle.Width / 2f, e.Item.Height / 2f);
        using var pen = new Pen(ItemColor(e.Item), 1.7f * scale)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(pen, new[] { new PointF(center.X - 4 * scale, center.Y), new PointF(center.X - scale, center.Y + 3 * scale), new PointF(center.X + 5 * scale, center.Y - 4 * scale) });
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var scale = Scale(e.ToolStrip);
        using var pen = new Pen(Separator);
        e.Graphics.DrawLine(pen, 11 * scale, e.Item.Height / 2f, e.Item.Width - 11 * scale, e.Item.Height / 2f);
    }
}

internal sealed class TrayContextMenu : ContextMenuStrip
{
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 20 || Height < 20) return;
        using var path = TrayMenuRenderer.Rounded(new RectangleF(0, 0, Width, Height), 9 * DeviceDpi / 96f);
        var previous = Region; Region = new Region(path); previous?.Dispose();
    }
}
