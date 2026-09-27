using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodeRim.Windows.Views;

/// <summary>The small account-limit reading indicator, inactive while hidden or motion is reduced.</summary>
internal sealed class LimitActivityIndicator : FrameworkElement
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private int phase;
    private static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush),
        typeof(LimitActivityIndicator), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    private Brush Foreground => (Brush)GetValue(ForegroundProperty);
    internal bool IsRunning => timer.IsEnabled;
    internal LimitActivityIndicator()
    {
        Width = Height = 14; SetResourceReference(ForegroundProperty, "SecondaryText");
        timer.Tick += (_, _) => { phase = (phase + 1) % 12; InvalidateVisual(); };
        Loaded += (_, _) => { Motion.PolicyChanged += Configure; Configure(); };
        Unloaded += (_, _) => { Motion.PolicyChanged -= Configure; timer.Stop(); };
        IsVisibleChanged += (_, _) => Configure();
    }
    private void Configure()
    {
        if (IsLoaded && IsVisible && Motion.Enabled) timer.Start(); else timer.Stop();
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var pen = new Pen(Foreground, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        for (var tick = 0; tick < 12; tick++)
        {
            drawing.PushOpacity(0.2 + 0.8 * ((tick + phase) % 12) / 11);
            drawing.PushTransform(new RotateTransform(tick * 30, 7, 7));
            drawing.DrawLine(pen, new Point(7, 1), new Point(7, 3.5)); drawing.Pop(); drawing.Pop();
        }
    }
}
