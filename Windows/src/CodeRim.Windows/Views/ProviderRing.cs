using System.Diagnostics;
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
    private ProviderReading? reading;
    private AppSettings settings = AppSettings.Default;
    private bool active, waiting, refreshing, clockAttached, initialized;
    private double? targetPercent;
    private double targetSweep;
    private static DependencyProperty Animated(string name, double value) => DependencyProperty.Register(name, typeof(double), typeof(ProviderRing),
        new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));
    internal static readonly DependencyProperty SweepProperty = Animated("Sweep", 0);
    internal static readonly DependencyProperty PercentProperty = Animated("Percent", 0);
    private static readonly DependencyProperty RotationProperty = Animated("Rotation", 0);
    private static readonly DependencyProperty PressProperty = Animated("Press", 1);
    public string ProviderId { get; set; } = "codex";
    public ProviderReading? Reading { get => reading; set { reading = value; UpdateReading(); } }
    public AppSettings Settings { get => settings; set { settings = value; UpdateReading(); ConfigureClock(); } }
    public bool Active { get => active; set { active = value; ConfigureClock(); InvalidateVisual(); } }
    public bool Waiting { get => waiting; set { waiting = value; ConfigureClock(); InvalidateVisual(); } }
    public bool Refreshing
    {
        get => refreshing;
        set
        {
            if (refreshing == value) return;
            refreshing = value;
            Motion.To(this, PressProperty, value ? 0.93 : 1, 0.45, Motion.Spring(0.62), enabled: Animates);
            if (value) Motion.To(this, RotationProperty, (Math.Floor((double)GetValue(RotationProperty) / 360) + 1) * 360,
                NotchMotion.Refresh, new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }, enabled: Animates);
        }
    }
    public double Phase { get; set; }
    internal double Sweep => Math.Clamp((double)GetValue(SweepProperty), 0, 1);
    internal double RefreshRotation => (double)GetValue(RotationProperty);
    internal bool ClockRunning => clockAttached;
    private bool Animates => IsLoaded && IsVisible && Motion.Enabled && !settings.ReduceMotion;
    public ProviderRing()
    {
        Width = NotchMetrics.Ring; Height = NotchMetrics.CellHeight;
        Loaded += (_, _) => { Motion.PolicyChanged += PolicyChanged; UpdateReading(); ConfigureClock(); };
        Unloaded += (_, _) => { Motion.PolicyChanged -= PolicyChanged; StopClock(); Motion.Stop(this); };
        IsVisibleChanged += (_, _) => ConfigureClock();
    }
    private void PolicyChanged() { if (!Animates) Motion.Stop(this); ConfigureClock(); InvalidateVisual(); }
    private void UpdateReading()
    {
        var percent = reading?.Evaluated(DateTimeOffset.Now).Headline?.UsedPercent;
        var display = percent is { } raw && double.IsFinite(raw) && raw >= 0 && raw < 9223372036854775808d
            ? settings.ShowRemaining ? Math.Max(0, 100 - raw) : raw : (double?)null;
        var sweep = Math.Clamp(display / 100 ?? 0, 0, 1);
        if (!initialized || targetSweep != sweep || targetPercent != display || !Animates)
        {
            var animate = initialized && Animates;
            Motion.To(this, SweepProperty, sweep, sweep == 0 ? NotchMotion.ReadingReset : NotchMotion.Reading,
                sweep == 0 ? Motion.Smooth : Motion.Spring(0.9), enabled: animate);
            Motion.To(this, PercentProperty, display ?? 0, NotchMotion.Reading, Motion.Spring(0.9), enabled: animate && targetPercent.HasValue && display.HasValue);
            initialized = reading is not null; targetSweep = sweep; targetPercent = display;
        }
        InvalidateVisual();
    }
    private void ConfigureClock()
    {
        var needed = Animates && (Active || Waiting || settings.RingColor == RingColorMode.Gradient && settings.AnimateGradient);
        if (needed && !clockAttached) { CompositionTarget.Rendering += Frame; clockAttached = true; }
        else if (!needed) StopClock();
    }
    private void StopClock() { if (clockAttached) CompositionTarget.Rendering -= Frame; clockAttached = false; }
    private void Frame(object? sender, EventArgs e)
    {
        if (!Animates) { StopClock(); return; }
        Phase = NotchMotion.Phase(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, NotchMotion.GradientPeriod);
        InvalidateVisual();
    }
    internal string AccessibleReading()
    {
        var value = reading?.Evaluated(DateTimeOffset.Now);
        var amount = value?.Headline?.UsedPercent is { } used
            ? (settings.ShowRemaining ? LimitFormatting.Halves(used).Left : Percent(used)) + (settings.ShowRemaining ? "% remaining" : "% used")
            : value?.Headline?.UsedCount is { } count ? TokenFormatter.Format(count, settings.NumberStyle) + " used"
            : value?.Headline?.RemainingCount is { } left ? TokenFormatter.Format(left, settings.NumberStyle) + " remaining" : "Usage unavailable";
        var state = value?.State switch
        {
            ReadingState.NeedsAuth => "Sign in required", ReadingState.Partial => "Partial reading", ReadingState.Stale => "Stale reading",
            ReadingState.Error => "Connection error", ReadingState.Loading => "Loading", ReadingState.Disabled => "Disabled",
            ReadingState.Unsupported => "Unsupported", ReadingState.Unavailable => "Unavailable", _ => null
        };
        return string.Join("; ", new[] { amount, state, Waiting ? "Waiting for input" : Active ? "Session active" : null, Refreshing ? "Refreshing" : null }.Where(x => x is not null));
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var center = new Point(22, 22); var radius = 22 - NotchMetrics.Track / 2;
        var activityRadius = 36 * NotchMetrics.Unit; var activityStroke = 5.5 * NotchMetrics.Unit;
        var value = reading?.Evaluated(DateTimeOffset.Now);
        var percent = value?.Headline?.UsedPercent;
        var fraction = Sweep;
        var stale = value?.State is ReadingState.Stale or ReadingState.Error;
        dc.PushTransform(new ScaleTransform((double)GetValue(PressProperty), (double)GetValue(PressProperty), 22, 22));
        dc.PushOpacity(stale ? 0.45 : 1);
        dc.DrawEllipse(null, new Pen(Ui.Brush("#303030"), NotchMetrics.Track), center, radius, radius);
        var color = settings.RingColor == RingColorMode.Usage ? NotchGeometry.BandColor(percent, settings.AccentColor) : settings.AccentColor;
        var rotation = RefreshRotation * Math.PI / 180;
        var gradientPhase = settings.AnimateGradient && Animates ? Phase : 0;
        if (settings.RingColor != RingColorMode.Gradient) DrawArc(dc, Ui.Brush(color), radius, NotchMetrics.Progress, -Math.PI / 2 + rotation, fraction);
        for (var i = 0; settings.RingColor == RingColorMode.Gradient && i < Math.Ceiling(fraction * 120); i++)
        {
            var start = -Math.PI / 2 + rotation + i / 120d * Math.Tau;
            var end = -Math.PI / 2 + rotation + Math.Min(fraction, (i + 1) / 120d) * Math.Tau;
            var brush = settings.RingColor == RingColorMode.Gradient ? Gradient(i / 120d + gradientPhase) : Ui.Brush(color);
            dc.DrawLine(new Pen(brush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                new Point(22 + Math.Cos(start) * radius, 22 + Math.Sin(start) * radius), new Point(22 + Math.Cos(end) * radius, 22 + Math.Sin(end) * radius));
        }
        dc.PushOpacity(percent >= 100 ? 0.35 : 1);
        ProviderMark.Draw(dc, ProviderId, new Rect(13.35, 13.35, 17.3, 17.3)); dc.Pop(); dc.Pop();
        var seconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (Waiting)
        {
            var alpha = Animates ? 0.3 + 0.7 * (1 + Math.Cos(Math.Tau * NotchMotion.Phase(seconds, NotchMotion.WaitingPeriod))) / 2 : 1;
            dc.PushOpacity(alpha); dc.DrawEllipse(null, new Pen(Ui.Brush("#F2FF00"), activityStroke), center, activityRadius, activityRadius); dc.Pop();
        }
        else if (Active)
        {
            var start = (Animates ? NotchMotion.Phase(seconds, NotchMotion.ActivityPeriod) : 0) * Math.Tau;
            DrawArc(dc, Brushes.White, activityRadius, activityStroke, start, 0.25);
        }
        dc.Pop();
        var label = percent is not null ? targetPercent is null ? "—" : (settings.ShowRemaining
                ? LimitFormatting.Halves(Math.Clamp(100 - (double)GetValue(PercentProperty), 0, 100)).Left
                : Percent(Math.Max((double)GetValue(PercentProperty), 0))) + "%"
            : value?.Headline?.UsedCount is { } count ? TokenFormatter.Format(count, settings.NumberStyle)
            : value?.Headline?.RemainingCount is { } remaining ? TokenFormatter.Format(remaining, settings.NumberStyle) : "—";
        var formatted = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 14, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(22 - formatted.Width / 2, 54));
    }
    private static void DrawArc(DrawingContext dc, Brush brush, double radius, double stroke, double start, double fraction)
    {
        if (fraction <= 0) return;
        var pen = new Pen(brush, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (fraction >= 1) { dc.DrawEllipse(null, pen, new Point(22, 22), radius, radius); return; }
        var end = start + fraction * Math.Tau; var path = new StreamGeometry();
        using (var context = path.Open())
        {
            context.BeginFigure(new Point(22 + Math.Cos(start) * radius, 22 + Math.Sin(start) * radius), false, false);
            context.ArcTo(new Point(22 + Math.Cos(end) * radius, 22 + Math.Sin(end) * radius), new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        path.Freeze(); dc.DrawGeometry(null, pen, path);
    }
    private static string Percent(double value) => LimitFormatting.Percent(value);
    private SolidColorBrush Gradient(double position)
    {
        string[] colors = settings.Gradient switch
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
    public static readonly DependencyProperty ProviderIdProperty = DependencyProperty.Register(nameof(ProviderId), typeof(string), typeof(ProviderMark), new FrameworkPropertyMetadata("codex", FrameworkPropertyMetadataOptions.AffectsRender));
    public string ProviderId { get => (string)GetValue(ProviderIdProperty); set => SetValue(ProviderIdProperty, value); }
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(ProviderMark), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    protected override void OnRender(DrawingContext dc) => Draw(dc, ProviderId, new Rect(0, 0, ActualWidth, ActualHeight), Foreground);
    internal static void Draw(DrawingContext dc, string id, Rect target, Brush? foreground = null)
    {
        if (Glyph(id) is not { } glyph)
        {
            var name = ProviderCatalog.Find(id)?.Name ?? "?";
            var text = new FormattedText(name[..1], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), target.Height, foreground ?? Brushes.White, 1);
            dc.DrawText(text, new Point(target.X + (target.Width - text.Width) / 2, target.Y)); return;
        }
        var bounds = glyph.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
        if (foreground is not null)
        {
            dc.PushOpacityMask(new DrawingBrush(glyph) { Stretch = Stretch.Uniform });
            dc.DrawRectangle(foreground, null, target); dc.Pop(); return;
        }
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
