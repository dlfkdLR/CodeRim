using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class NotchWindow
{
    private NotchShape? movingShape;
    private int foldRevision, popupRevision;
    internal double FoldProgress => movingShape?.Expansion ?? (Expanded ? 1 : 0);
    private bool Animates => Motion.Enabled && !settings.Current.ReduceMotion && IsVisible;
    internal void SetExpanded(bool value)
    {
        foldTimer.Stop(); expanded = value;
        if (value && !buttons.Keys.SequenceEqual(VisibleProviderIds(), StringComparer.Ordinal))
        { Render(animateOpening: true, openingProgress: movingShape?.Expansion ?? 0); return; }
        if (movingShape is null) { Render(animateOpening: Expanded); return; }
        AnimateFold();
    }
    private void AnimateFold()
    {
        if (movingShape is null) return;
        var revision = ++foldRevision; var open = Expanded; var target = movingShape;
        var shift = 28 * NotchMetrics.Unit;
        var index = 0;
        foreach (var button in buttons.Values)
        {
            var slide = button.RenderTransform as TranslateTransform ?? new TranslateTransform();
            button.RenderTransform = slide; button.IsHitTestVisible = open;
            var x = settings.Current.Edge == NotchEdge.Left ? -shift : settings.Current.Edge == NotchEdge.Right ? shift : 0;
            var y = settings.Current.Edge == NotchEdge.Top ? -shift : settings.Current.Edge == NotchEdge.Bottom ? shift : 0;
            var delay = open ? NotchMotion.Stagger(index++) : 0;
            Motion.To(button, OpacityProperty, open ? 1 : 0, NotchMotion.Contents, delay: delay, enabled: Animates);
            Motion.To(slide, TranslateTransform.XProperty, open ? 0 : x, NotchMotion.Contents * 2, Motion.Spring(0.82), delay, enabled: Animates);
            Motion.To(slide, TranslateTransform.YProperty, open ? 0 : y, NotchMotion.Contents * 2, Motion.Spring(0.82), delay, enabled: Animates);
        }
        if (settingsControl is not null)
            Motion.To(settingsControl, OpacityProperty, open ? 1 : 0, open ? NotchMotion.Contents : 0.2,
                delay: open ? NotchMotion.Stagger(buttons.Count) : 0, enabled: Animates);
        if (!open) HideControlsForFold();
        Motion.To(target, NotchShape.ExpansionProperty, open ? 1 : 0, NotchMotion.Unfold * 2, Motion.Spring(0.78),
            completed: () => { if (revision == foldRevision && !Expanded && !closed) Render(); }, enabled: Animates);
    }
    private static readonly DependencyProperty PopupXProperty = DependencyProperty.Register("PopupX", typeof(double), typeof(NotchWindow),
        new PropertyMetadata(0d, (o, e) => ((NotchWindow)o).popup.HorizontalOffset = (double)e.NewValue));
    private static readonly DependencyProperty PopupYProperty = DependencyProperty.Register("PopupY", typeof(double), typeof(NotchWindow),
        new PropertyMetadata(0d, (o, e) => ((NotchWindow)o).popup.VerticalOffset = (double)e.NewValue));
    internal Point PopupAnchor => new((double)GetValue(PopupXProperty), (double)GetValue(PopupYProperty));
    private void UpdatePopupAnchor(bool animate)
    {
        var center = Vertical ? new Point(bodyDepth / 2, bodyStart + bodyLength / 2) : new Point(bodyStart + bodyLength / 2, bodyDepth / 2);
        if (hovered is not null && buttons.TryGetValue(hovered, out var button) && button.IsLoaded)
            center = button.TranslatePoint(new Point(button.ActualWidth / 2, NotchMetrics.Ring / 2), this);
        Motion.To(this, PopupXProperty, center.X, NotchMotion.Glide, Motion.Spring(0.86), enabled: animate && Animates);
        Motion.To(this, PopupYProperty, center.Y, NotchMotion.Glide, Motion.Spring(0.86), enabled: animate && Animates);
    }
    private void RevealPopup(bool transition)
    {
        popupRevision++; Motion.Stop(popupFrame); popupFrame.Opacity = 1; popupFrame.IsHitTestVisible = true;
        popup.IsOpen = true;
        if (transition && Animates) Motion.Enter(popupFrame);
    }
    private void FadeProviderPopup()
    {
        var revision = ++popupRevision;
        popupFrame.IsHitTestVisible = false;
        Motion.To(popupFrame, OpacityProperty, 0, NotchMotion.Crossfade,
            completed: () => { if (revision == popupRevision && !accountMenu) popup.IsOpen = false; }, enabled: Animates);
    }
}
