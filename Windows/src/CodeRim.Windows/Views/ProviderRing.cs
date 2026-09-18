using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal sealed class ProviderRing : FrameworkElement
{
    private static readonly Dictionary<string, Geometry?> Glyphs = new(StringComparer.Ordinal);
    public string ProviderId { get; set; } = "codex";
    public ProviderReading? Reading { get; set; }
    public AppSettings Settings { get; set; } = AppSettings.Default;
    public bool Active { get; set; }
    public double Phase { get; set; }
    public ProviderRing() { Width = 60; Height = 74; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var center = new Point(30, 25);
        dc.DrawEllipse(null, new Pen(Ui.Brush("#303030"), 4), center, 20, 20);
        var percent = Reading?.Headline?.UsedPercent;
        var fraction = percent.HasValue ? Math.Clamp((Settings.ShowRemaining ? 100 - percent.Value : percent.Value) / 100, 0, 1) : 0;
        var color = Settings.RingColor == RingColorMode.Usage ? NotchGeometry.BandColor(percent) : Settings.Accent;
        var segments = Math.Max(0, (int)Math.Ceiling(fraction * 100));
        for (var i = 0; i < segments; i++)
        {
            var start = -Math.PI / 2 + i / 100d * Math.PI * 2;
            var end = -Math.PI / 2 + Math.Min(fraction, (i + 1) / 100d) * Math.PI * 2;
            var brush = Settings.RingColor == RingColorMode.Gradient ? Gradient(i / 100d + Phase) : Ui.Brush(color);
            dc.DrawLine(new Pen(brush, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                new Point(center.X + Math.Cos(start) * 20, center.Y + Math.Sin(start) * 20), new Point(center.X + Math.Cos(end) * 20, center.Y + Math.Sin(end) * 20));
        }
        if (Active) dc.DrawEllipse(Ui.Brush("#00FF88"), null, new Point(48, 8), 3, 3);
        if (Glyph(ProviderId) is { } glyph)
        {
            var bounds = glyph.Bounds;
            var scale = 20 / Math.Max(bounds.Width, bounds.Height);
            dc.PushTransform(new TranslateTransform(center.X - bounds.Width * scale / 2, center.Y - bounds.Height * scale / 2));
            dc.PushTransform(new ScaleTransform(scale, scale)); dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            dc.DrawGeometry(Brushes.White, null, glyph); dc.Pop(); dc.Pop(); dc.Pop();
        }
        else DrawText(dc, ProviderCatalog.Find(ProviderId)?.Name[..1] ?? "?", 18, new Point(30, 13));
        var label = percent is { } used ? Percent(Settings.ShowRemaining ? Math.Clamp(100 - used, 0, 100) : used) + "%"
            : Reading?.Headline?.UsedCount is { } count ? TokenFormatter.Format(count, Settings.NumberStyle)
            : Reading?.Headline?.RemainingCount is { } remaining ? TokenFormatter.Format(remaining, Settings.NumberStyle) : "—";
        DrawText(dc, label, 13, new Point(30, 52));
        Opacity = Reading?.State is ReadingState.Stale or ReadingState.Error ? 0.6 : 1;
    }
    private static string Percent(double value) => value is > 0 and < 0.1 ? "<0.1" : value is > 0 and < 1 ? value.ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
    private void DrawText(DrawingContext dc, string text, double size, Point center)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y));
    }
    private SolidColorBrush Gradient(double position)
    {
        string[] colors = Settings.Gradient switch
        {
            "Ocean" => ["#54D9FF", "#4A8DFF", "#777BFF", "#54D9FF"],
            "Sunset" => ["#FFCA70", "#FF8C69", "#F879BD", "#FFCA70"],
            "Spectrum" => ["#FF7C99", "#FFD46B", "#77E3A4", "#67C8FF", "#B39AFF", "#FF7C99"],
            _ => ["#52E5C5", "#61A8FF", "#BA88FF", "#52E5C5"]
        };
        var point = ((position % 1 + 1) % 1) * (colors.Length - 1); var i = (int)point; var mix = point - i;
        var a = Ui.Brush(colors[i]).Color; var b = Ui.Brush(colors[i + 1]).Color;
        return new SolidColorBrush(Color.FromRgb((byte)(a.R + (b.R - a.R) * mix), (byte)(a.G + (b.G - a.G) * mix), (byte)(a.B + (b.B - a.B) * mix)));
    }
    private static Geometry? Glyph(string id)
    {
        if (Glyphs.TryGetValue(id, out var found)) return found;
        try
        {
            var file = id switch { "codex" => "OpenAI.svg", "claude" => "Claude.svg", _ => "ProviderIcon-" + id + ".svg" };
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ProviderLogos/" + file));
            if (resource is null) return Glyphs[id] = null;
            using var stream = resource.Stream;
            var xml = XDocument.Load(stream); var group = new GeometryGroup();
            foreach (var path in xml.Descendants().Where(x => x.Name.LocalName == "path"))
                if (path.Attribute("d")?.Value is { } data) group.Children.Add(Geometry.Parse(data));
            group.Freeze(); return Glyphs[id] = group.Children.Count > 0 ? group : null;
        }
        catch (Exception e) when (e is System.IO.IOException or FormatException or System.Xml.XmlException) { return Glyphs[id] = null; }
    }
}
