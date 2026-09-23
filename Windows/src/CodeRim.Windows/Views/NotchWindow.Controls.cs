using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using Button = System.Windows.Controls.Button;

namespace CodeRim.Windows.Views;

internal sealed partial class NotchWindow
{
    private readonly DispatcherTimer controlHide = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Button? settingsControl, accountControl;
    private Border? controlRail;
    private NotchSettingsGlyph? settingsGlyph;
    private int controlsRevision;
    private double controlDirection;
    internal bool ControlsRevealed { get; private set; }

    private void ResetControls()
    {
        controlsRevision++; controlHide.Stop(); ControlsRevealed = false;
        settingsControl = null; accountControl = null; controlRail = null; settingsGlyph = null;
    }
    private void RenderControls(Canvas canvas, bool atStart)
    {
        var scale = settings.Current.Scale; controlDirection = atStart ? -1 : 1;
        var along = bodyStart + (atStart ? 0 : bodyLength);
        var next = along + (atStart ? -1 : 1) * NotchMetrics.ControlSpacing * scale;
        Point Center(double value) => settings.Current.Edge switch
        {
            NotchEdge.Left => new(NotchMetrics.Curl * scale, value),
            NotchEdge.Right => new(Width - NotchMetrics.Curl * scale, value),
            NotchEdge.Top => new(value, NotchMetrics.Curl * scale),
            _ => new(value, Height - NotchMetrics.Curl * scale)
        };
        var gearCenter = Center(along); var accountCenter = Center(next);
        var diameter = NotchMetrics.Control * scale;
        var distance = NotchMetrics.ControlSpacing * scale;
        var rail = new Border { Background = Brushes.Black, CornerRadius = new(diameter / 2),
            Width = Vertical ? diameter : diameter + distance, Height = Vertical ? diameter + distance : diameter,
            Visibility = Visibility.Hidden };
        AutomationProperties.SetAutomationId(rail, "notch.controls");
        Canvas.SetLeft(rail, Math.Min(gearCenter.X, accountCenter.X) - diameter / 2);
        Canvas.SetTop(rail, Math.Min(gearCenter.Y, accountCenter.Y) - diameter / 2);
        canvas.Children.Add(rail); controlRail = rail;

        var glyph = new NotchSettingsGlyph(settings.Current.Edge, atStart) { Width = NotchSettingsGlyph.Extent, Height = NotchSettingsGlyph.Extent,
            IsHitTestVisible = false, LayoutTransform = new ScaleTransform(scale, scale) };
        var gear = Control("", "Open Settings", () => openSettings(null));
        gear.Width = gear.Height = NotchMetrics.OrbHotZone * scale; gear.Margin = new(0); gear.Content = glyph;
        // A one-alpha hit surface keeps the resting arc's center reachable on a layered
        // native window; fully transparent pixels otherwise pass through to the desktop.
        gear.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        AutomationProperties.SetAutomationId(gear, "notch.settings");
        PlaceControl(canvas, gear, gearCenter); settingsControl = gear; settingsGlyph = glyph;
        var accounts = Control("\uE895", "Switch account", OpenAccounts);
        accounts.Width = accounts.Height = diameter; accounts.Margin = new(0); accounts.Visibility = Visibility.Hidden;
        if (accounts.Content is TextBlock accountIcon) accountIcon.FontSize = NotchMetrics.OrbGlyph * 0.7 * scale;
        AutomationProperties.SetAutomationId(accounts, "notch.switchAccount");
        PlaceControl(canvas, accounts, accountCenter); accountControl = accounts;
        gear.MouseEnter += (_, _) => RevealControls();
        gear.GotKeyboardFocus += (_, _) => RevealControls();
        foreach (var target in new FrameworkElement[] { gear, accounts, rail })
        {
            target.MouseEnter += (_, _) => controlHide.Stop();
            target.MouseLeave += (_, _) => controlHide.Start();
            target.LostKeyboardFocus += (_, _) => controlHide.Start();
        }
    }
    private static void PlaceControl(Canvas canvas, Button button, Point center)
    {
        Canvas.SetLeft(button, center.X - button.Width / 2); Canvas.SetTop(button, center.Y - button.Height / 2);
        canvas.Children.Add(button);
    }
    private void RevealControls()
    {
        controlHide.Stop(); ControlsRevealed = settingsControl is not null;
        AnimateControls(true);
    }
    private void HideControlsIfUnused()
    {
        if (accountMenu && popup.IsOpen || settingsControl is { IsMouseOver: true } or { IsKeyboardFocusWithin: true }
            || accountControl is { IsMouseOver: true } or { IsKeyboardFocusWithin: true }
            || controlRail is { IsMouseOver: true }) return;
        controlHide.Stop(); ControlsRevealed = false;
        AnimateControls(false);
    }
    private void HideControlsForFold() { controlHide.Stop(); ControlsRevealed = false; AnimateControls(false); }
    private void AnimateControls(bool show)
    {
        var revision = ++controlsRevision;
        if (settingsGlyph is not null) Motion.To(settingsGlyph, NotchSettingsGlyph.RevealProperty, show ? 1 : 0, 0.3, enabled: Animates);
        if (controlRail is not null)
        {
            if (show && controlRail.Visibility != Visibility.Visible) { controlRail.Opacity = 0; controlRail.Visibility = Visibility.Visible; }
            var rail = controlRail;
            Motion.To(rail, OpacityProperty, show ? 1 : 0, NotchMotion.Crossfade,
                completed: () => { if (!show && revision == controlsRevision) rail.Visibility = Visibility.Hidden; }, enabled: Animates);
        }
        if (accountControl is not null)
        {
            var account = accountControl;
            var shift = 24 * NotchMetrics.Unit * settings.Current.Scale;
            if (account.RenderTransform is not TransformGroup)
            {
                var transforms = new TransformGroup(); transforms.Children.Add(new ScaleTransform(0.65, 0.65));
                transforms.Children.Add(new TranslateTransform(Vertical ? 0 : -shift * controlDirection, Vertical ? -shift * controlDirection : 0));
                account.RenderTransform = transforms; account.RenderTransformOrigin = new Point(0.5, 0.5); account.Opacity = 0;
            }
            var group = (TransformGroup)account.RenderTransform;
            var scale = (ScaleTransform)group.Children[0]; var slide = (TranslateTransform)group.Children[1];
            if (show) account.Visibility = Visibility.Visible;
            account.IsHitTestVisible = show; account.Focusable = show;
            var delay = show ? 0.08 : 0;
            Motion.To(scale, ScaleTransform.ScaleXProperty, show ? 1 : 0.65, 0.6, Motion.Spring(0.82), delay, enabled: Animates);
            Motion.To(scale, ScaleTransform.ScaleYProperty, show ? 1 : 0.65, 0.6, Motion.Spring(0.82), delay, enabled: Animates);
            Motion.To(slide, TranslateTransform.XProperty, show || Vertical ? 0 : -shift * controlDirection, 0.6, Motion.Spring(0.82), delay, enabled: Animates);
            Motion.To(slide, TranslateTransform.YProperty, show || !Vertical ? 0 : -shift * controlDirection, 0.6, Motion.Spring(0.82), delay, enabled: Animates);
            Motion.To(account, OpacityProperty, show ? 1 : 0, 0.3, delay: delay,
                completed: () => { if (!show && revision == controlsRevision) account.Visibility = Visibility.Hidden; }, enabled: Animates);
        }
    }
}

/// <summary>Same resting quarter arc as macOS SettingsOrb; the glyph is upright for every edge.</summary>
internal sealed class NotchSettingsGlyph(NotchEdge edge, bool atStart) : FrameworkElement
{
    internal static double Extent => 2 * NotchMetrics.OrbArcRadius + NotchMetrics.OrbStroke;
    internal static readonly DependencyProperty RevealProperty = DependencyProperty.Register("Reveal", typeof(double), typeof(NotchSettingsGlyph),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    protected override void OnRender(DrawingContext drawing)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var reveal = Math.Clamp((double)GetValue(RevealProperty), 0, 1);
        if (reveal > 0)
        {
            drawing.PushOpacity(reveal);
            var text = new FormattedText("\uE713", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"),
                NotchMetrics.OrbGlyph * 0.8, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2)); drawing.Pop();
        }
        drawing.PushOpacity(1 - reveal);
        var start = NotchMetrics.RestingArcStart(edge, atStart) * Math.Tau;
        Point At(double angle) => new(center.X + Math.Cos(angle) * NotchMetrics.OrbArcRadius, center.Y + Math.Sin(angle) * NotchMetrics.OrbArcRadius);
        var path = new StreamGeometry();
        using (var context = path.Open())
        {
            context.BeginFigure(At(start), false, false);
            context.ArcTo(At(start + Math.PI / 2), new Size(NotchMetrics.OrbArcRadius, NotchMetrics.OrbArcRadius), 0, false, SweepDirection.Clockwise, true, false);
        }
        path.Freeze();
        drawing.DrawGeometry(null, new Pen(Brushes.Black, NotchMetrics.OrbStroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, path);
        drawing.Pop();
    }
}
