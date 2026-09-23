using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Views;

/// <summary>Animate exact integer totals without converting stored counts to floating point.</summary>
internal sealed class AnimatedMetric : TextBlock
{
    private readonly long from, to;
    private readonly TokenNumberStyle style;
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double), typeof(AnimatedMetric),
        new PropertyMetadata(1d, (o, _) => ((AnimatedMetric)o).UpdateText()));
    internal AnimatedMetric(long previous, long current, TokenNumberStyle numberStyle, double size, bool animate)
    {
        from = previous; to = current; style = numberStyle;
        FontSize = size; FontWeight = FontWeights.SemiBold; TextWrapping = TextWrapping.NoWrap; Margin = new Thickness(0, 0, 0, 6);
        System.Windows.Documents.Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);
        SetResourceReference(ForegroundProperty, "PrimaryText");
        SetValue(ProgressProperty, 0d);
        Motion.To(this, ProgressProperty, 1, 0.24, new QuadraticEase { EasingMode = EasingMode.EaseOut }, enabled: animate && Motion.Enabled);
        UpdateText(); Unloaded += (_, _) => Motion.Stop(this);
    }
    private void UpdateText()
    {
        var progress = Math.Clamp((double)GetValue(ProgressProperty), 0, 1);
        var value = progress >= 1 ? to : (long)decimal.Round(from + ((decimal)to - from) * (decimal)progress);
        Text = TokenFormatter.Format(value, style);
    }
}
