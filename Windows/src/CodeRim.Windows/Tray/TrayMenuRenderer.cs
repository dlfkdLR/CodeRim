using System.Drawing;
using System.Windows.Forms;

namespace CodeRim.Windows.Tray;

// Windows owns the menu border, background, selection, checks and separators.
// Only the existing command glyphs need a foreground that follows OS selection
// and high contrast rather than the separate WPF application palette.
internal sealed class TrayMenuRenderer : ToolStripSystemRenderer
{
    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Item.Tag is TrayMenuSymbol symbol)
        {
            var color = !e.Item.Enabled ? SystemColors.GrayText
                : e.Item.Selected ? SystemColors.HighlightText : SystemColors.MenuText;
            TrayMenuGlyph.Draw(e.Graphics, e.ImageRectangle, symbol, color);
        }
        else base.OnRenderItemImage(e);
    }
}
