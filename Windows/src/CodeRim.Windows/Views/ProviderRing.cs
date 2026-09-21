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
    public string ProviderId { get; set; } = "codex";
    public ProviderReading? Reading { get; set; }
    public AppSettings Settings { get; set; } = AppSettings.Default;
    public bool Active { get; set; }
    public bool Waiting { get; set; }
    public bool Refreshing { get; set; }
    public double Phase { get; set; }
    public ProviderRing() { Width = NotchMetrics.Ring; Height = NotchMetrics.CellHeight; }
    internal string AccessibleReading()
    {
        var reading = Reading?.Evaluated(DateTimeOffset.Now);
        var amount = reading?.Headline?.UsedPercent is { } used
            ? Percent(Settings.ShowRemaining ? Math.Clamp(100 - used, 0, 100) : used) + (Settings.ShowRemaining ? "% remaining" : "% used")
            : reading?.Headline?.UsedCount is { } count ? TokenFormatter.Format(count, Settings.NumberStyle) + " used"
            : reading?.Headline?.RemainingCount is { } left ? TokenFormatter.Format(left, Settings.NumberStyle) + " remaining"
            : "Usage unavailable";
        var state = reading?.State switch
        {
            ReadingState.NeedsAuth => "Sign in required",
            ReadingState.Partial => "Partial reading",
            ReadingState.Stale => "Stale reading",
            ReadingState.Error => "Connection error",
            ReadingState.Loading => "Loading",
            ReadingState.Disabled => "Disabled",
            ReadingState.Unsupported => "Unsupported",
            ReadingState.Unavailable => "Unavailable",
            _ => null
        };
        return string.Join("; ", new[] { amount, state, Waiting ? "Waiting for input" : Active ? "Session active" : null, Refreshing ? "Refreshing" : null }.Where(x => x is not null));
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var center = new Point(22, 22); const double radius = 19;
        var reading = Reading?.Evaluated(DateTimeOffset.Now);
        var stale = reading?.State is ReadingState.Stale or ReadingState.Error;
        dc.PushOpacity(stale ? 0.6 : 1);
        dc.DrawEllipse(null, new Pen(Ui.Brush("#303030"), 5.8), center, radius, radius);
        var percent = reading?.Headline?.UsedPercent;
        var fraction = percent.HasValue ? Math.Clamp((Settings.ShowRemaining ? 100 - percent.Value : percent.Value) / 100, 0, 1) : 0;
        var color = Settings.RingColor == RingColorMode.Usage ? NotchGeometry.BandColor(percent) : Settings.Accent;
        for (var i = 0; i < Math.Ceiling(fraction * 120); i++)
        {
            var start = -Math.PI / 2 + i / 120d * Math.PI * 2;
            var end = -Math.PI / 2 + Math.Min(fraction, (i + 1) / 120d) * Math.PI * 2;
            var brush = Settings.RingColor == RingColorMode.Gradient ? Gradient(i / 120d + Phase) : Ui.Brush(color);
            dc.DrawLine(new Pen(brush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                new Point(center.X + Math.Cos(start) * radius, center.Y + Math.Sin(start) * radius), new Point(center.X + Math.Cos(end) * radius, center.Y + Math.Sin(end) * radius));
        }
        ProviderMark.Draw(dc, ProviderId, new Rect(13.35, 13.35, 17.3, 17.3));
        if (Active || Refreshing)
        {
            var angle = Phase * Math.PI * 2;
            dc.DrawEllipse(Brushes.White, null, new Point(22 + Math.Sin(angle) * 13, 22 - Math.Cos(angle) * 13), 1.5, 1.5);
        }
        if (Waiting) dc.DrawEllipse(Ui.Brush("#F2FF00"), null, new Point(39, 4), 3, 3);
        var label = percent is { } used ? Percent(Settings.ShowRemaining ? Math.Clamp(100 - used, 0, 100) : used) + "%"
            : reading?.Headline?.UsedCount is { } count ? TokenFormatter.Format(count, Settings.NumberStyle)
            : reading?.Headline?.RemainingCount is { } remaining ? TokenFormatter.Format(remaining, Settings.NumberStyle) : "—";
        var formatted = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 14, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(22 - formatted.Width / 2, 54));
        dc.Pop();
    }
    private static string Percent(double value) => value is > 0 and < 0.1 ? "<0.1" : value is > 0 and < 1 ? value.ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
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
}

internal sealed class ProviderMark : FrameworkElement
{
    private static readonly Dictionary<string, DrawingGroup?> Glyphs = new(StringComparer.Ordinal);
    public string ProviderId { get; set; } = "codex";
    protected override void OnRender(DrawingContext dc) => Draw(dc, ProviderId, new Rect(0, 0, ActualWidth, ActualHeight));
    internal static void Draw(DrawingContext dc, string id, Rect target)
    {
        if (Glyph(id) is not { } glyph)
        {
            var name = ProviderCatalog.Find(id)?.Name ?? "?";
            var text = new FormattedText(name[..1], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), target.Height, Brushes.White, 1);
            dc.DrawText(text, new Point(target.X + (target.Width - text.Width) / 2, target.Y)); return;
        }
        var bounds = glyph.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
        var scale = Math.Min(target.Width / bounds.Width, target.Height / bounds.Height);
        dc.PushTransform(new TranslateTransform(target.X + (target.Width - bounds.Width * scale) / 2, target.Y + (target.Height - bounds.Height * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale)); dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
        dc.DrawDrawing(glyph); dc.Pop(); dc.Pop(); dc.Pop();
    }
    internal static bool HasGlyph(string id) => Glyph(id) is { Bounds.IsEmpty: false } glyph && glyph.Bounds.Width > 0 && glyph.Bounds.Height > 0;
    private static DrawingGroup? Glyph(string id)
    {
        if (Glyphs.TryGetValue(id, out var found)) return found;
        try
        {
            var file = id switch {
                "codex" or "openai" or "azureopenai" => "OpenAI.svg", "claude" => "Claude.svg",
                "copilot" or "cursor" or "grok" or "commandcode" or "glm" => "Glyph-" + id + ".svg",
                "gemini" => "Glyph-antigravity.svg", "gemini-cli" => "Glyph-gemini.svg",
                "ollama" or "ollama-local" => "Glyph-ollama.svg", "opencode-zen" => "ProviderIcon-opencode.svg",
                "alibabatokenplan" => "ProviderIcon-alibaba.svg", "moonshot" => "ProviderIcon-kimi.svg",
                _ => "ProviderIcon-" + id + ".svg" };
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ProviderLogos/" + file));
            if (resource is null) return Glyphs[id] = null;
            using var stream = resource.Stream;
            var xml = XDocument.Load(stream); var group = new DrawingGroup();
            foreach (var element in xml.Descendants())
            {
                if (element.Ancestors().Any(x => x.Name.LocalName is "defs" or "clipPath" or "mask" or "symbol")) continue;
                string? Style(string name)
                {
                    foreach (var node in element.AncestorsAndSelf())
                    {
                        var pair = (node.Attribute("style")?.Value ?? "").Split(';').Select(x => x.Split(':', 2)).FirstOrDefault(x => x.Length == 2 && x[0].Trim() == name);
                        if (pair is not null) return pair[1].Trim();
                        if (node.Attribute(name)?.Value is { } value) return value;
                    }
                    return null;
                }
                double Number(string name) => double.TryParse(element.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
                Geometry? geometry = element.Name.LocalName switch
                {
                    "path" when element.Attribute("d")?.Value is { } data => Geometry.Parse((Style("fill-rule") == "evenodd" ? "F0 " : "F1 ") + data),
                    "rect" => new RectangleGeometry(new Rect(Number("x"), Number("y"), Number("width"), Number("height")), Number("rx"), Number("ry") == 0 ? Number("rx") : Number("ry")),
                    "circle" => new EllipseGeometry(new Point(Number("cx"), Number("cy")), Number("r"), Number("r")),
                    "line" => new LineGeometry(new Point(Number("x1"), Number("y1")), new Point(Number("x2"), Number("y2"))),
                    "polygon" or "polyline" when element.Attribute("points")?.Value is { } points => Geometry.Parse("M " + points + (element.Name.LocalName == "polygon" ? " Z" : "")),
                    "ellipse" => new EllipseGeometry(new Point(Number("cx"), Number("cy")), Number("rx"), Number("ry")),
                    _ => null
                };
                if (geometry is not null)
                {
                    var transforms = new TransformGroup();
                    foreach (var node in element.AncestorsAndSelf().Reverse())
                    foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(node.Attribute("transform")?.Value ?? "", @"(translate|scale|matrix|rotate)\s*\(([^)]*)\)"))
                    {
                        var values = System.Text.RegularExpressions.Regex.Matches(match.Groups[2].Value, @"[-+]?(?:\d*\.)?\d+(?:[eE][-+]?\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
                        Transform? transform = match.Groups[1].Value switch
                        {
                            "translate" when values.Length >= 1 => new TranslateTransform(values[0], values.Length > 1 ? values[1] : 0),
                            "scale" when values.Length >= 1 => new ScaleTransform(values[0], values.Length > 1 ? values[1] : values[0]),
                            "matrix" when values.Length == 6 => new MatrixTransform(values[0], values[1], values[2], values[3], values[4], values[5]),
                            "rotate" when values.Length == 1 => new RotateTransform(values[0]),
                            "rotate" when values.Length == 3 => new RotateTransform(values[0], values[1], values[2]),
                            _ => null
                        };
                        if (transform is not null) transforms.Children.Insert(0, transform);
                    }
                    var fill = Style("fill") == "none" || element.Name.LocalName is "line" or "polyline" ? null : Brushes.White;
                    Pen? pen = null;
                    if (Style("stroke") is { } stroke && stroke != "none")
                    {
                        var strokeWidth = double.TryParse(Style("stroke-width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var width) ? width : 1;
                        pen = new Pen(Brushes.White, strokeWidth)
                        {
                            StartLineCap = Style("stroke-linecap") == "round" ? PenLineCap.Round : Style("stroke-linecap") == "square" ? PenLineCap.Square : PenLineCap.Flat,
                            EndLineCap = Style("stroke-linecap") == "round" ? PenLineCap.Round : Style("stroke-linecap") == "square" ? PenLineCap.Square : PenLineCap.Flat,
                            LineJoin = Style("stroke-linejoin") == "round" ? PenLineJoin.Round : Style("stroke-linejoin") == "bevel" ? PenLineJoin.Bevel : PenLineJoin.Miter
                        };
                    }
                    var drawing = new DrawingGroup { Transform = transforms };
                    drawing.Children.Add(new GeometryDrawing(fill, pen, geometry)); group.Children.Add(drawing);
                }
            }
            group.Freeze(); return Glyphs[id] = group.Children.Count > 0 ? group : null;
        }
        catch (Exception e) when (e is System.IO.IOException or FormatException or System.Xml.XmlException or InvalidOperationException or ArgumentException) { return Glyphs[id] = null; }
    }
}
