using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Views;

/// <summary>Animate exact integer totals without converting stored counts to floating point.</summary>
internal sealed class AnimatedMetric : TextBlock
{
    private long from, to;
    private TokenNumberStyle style;
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double), typeof(AnimatedMetric),
        new PropertyMetadata(1d, (o, _) => ((AnimatedMetric)o).UpdateText()));
    internal AnimatedMetric(long previous, long current, TokenNumberStyle numberStyle, double size, bool animate)
    {
        from = to = previous; style = numberStyle;
        FontSize = size; FontWeight = FontWeights.SemiBold; TextWrapping = TextWrapping.NoWrap; Margin = new Thickness(0, 0, 0, 6);
        System.Windows.Documents.Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);
        SetResourceReference(ForegroundProperty, "PrimaryText");
        Update(current, numberStyle, animate);
        Unloaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!IsLoaded) Motion.Stop(this); }));
    }
    internal long DisplayedValue
    {
        get
        {
            var progress = Math.Clamp((double)GetValue(ProgressProperty), 0, 1);
            return progress >= 1 ? to : (long)decimal.Round(from + ((decimal)to - from) * (decimal)progress);
        }
    }
    internal void Update(long value, TokenNumberStyle numberStyle, bool animate)
    {
        style = numberStyle;
        if (to == value) { UpdateText(); return; }
        var current = DisplayedValue;
        Motion.Stop(this); from = current; to = value;
        SetValue(ProgressProperty, 0d);
        Motion.To(this, ProgressProperty, 1, 0.24, new QuadraticEase { EasingMode = EasingMode.EaseOut }, enabled: animate && Motion.Enabled);
        UpdateText();
    }
    private void UpdateText() => Text = TokenFormatter.Format(DisplayedValue, style);
}
