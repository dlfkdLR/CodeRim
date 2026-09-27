using System.Windows;
using System.Windows.Media;
using CodeRim.Core.Domain;

namespace CodeRim.Windows.Views;

/// <summary>The Mac task row's clock-derived 1.4-second indicator, with no retained animation clock.</summary>
internal sealed class SessionStatusRing : FrameworkElement
{
    private readonly string state;
    private readonly Brush color;
    internal double Angle { get; private set; }
    internal bool IsTicking { get; private set; }
    internal SessionStatusRing(string state, Brush color)
    {
        this.state = state; this.color = color;
        Width = Height = NotchMetrics.StatusDot; IsHitTestVisible = false;
        Loaded += (_, _) => { Motion.PolicyChanged += UpdatePolicy; UpdatePolicy(); };
        Unloaded += (_, _) => { Motion.PolicyChanged -= UpdatePolicy; Stop(); };
        IsVisibleChanged += (_, _) => UpdatePolicy();
    }
    private void UpdatePolicy()
    {
        var ticking = state == "busy" && IsLoaded && IsVisible && Motion.Enabled;
        if (ticking == IsTicking) return;
        if (ticking) { IsTicking = true; CompositionTarget.Rendering += Tick; Tick(null, EventArgs.Empty); }
        else Stop();
    }
    private void Stop()
    {
        if (IsTicking) CompositionTarget.Rendering -= Tick;
        IsTicking = false; Angle = 0; InvalidateVisual();
    }
    private void Tick(object? sender, EventArgs e)
    {
        Angle = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1400 / 1400d * 360;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (state == "unavailable") return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - NotchMetrics.StatusDotStroke / 2);
        var pen = new Pen(color, NotchMetrics.StatusDotStroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (state == "idle") { drawingContext.DrawEllipse(null, pen, center, radius, radius); return; }
        var trim = state == "waiting" ? 0.5 : 0.75;
        Point At(double degrees)
        {
            var radians = degrees * Math.PI / 180;
            return new(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        }
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(At(Angle - 90), false, false);
            context.ArcTo(At(Angle - 90 + trim * 360), new Size(radius, radius), 0, trim > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze(); drawingContext.DrawGeometry(null, pen, geometry);
    }
}
