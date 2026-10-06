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
    private Rect railRest, railOpen;
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
        var scale = renderScale; controlDirection = atStart ? -1 : 1;
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
        // At rest the rail is the gear's own disc; it stretches toward the account control as that appears.
        var rail = new Border { Background = Brushes.Black, CornerRadius = new(diameter / 2),
            Width = diameter, Height = diameter, Visibility = Visibility.Hidden };
        AutomationProperties.SetAutomationId(rail, "notch.controls");
        Canvas.SetLeft(rail, gearCenter.X - diameter / 2);
        Canvas.SetTop(rail, gearCenter.Y - diameter / 2);
        canvas.Children.Add(rail); controlRail = rail;
        railRest = new(gearCenter.X - diameter / 2, gearCenter.Y - diameter / 2, diameter, diameter);
        railOpen = new(Math.Min(gearCenter.X, accountCenter.X) - diameter / 2, Math.Min(gearCenter.Y, accountCenter.Y) - diameter / 2,
            Vertical ? diameter : diameter + distance, Vertical ? diameter + distance : diameter);

        var glyph = new NotchSettingsGlyph(settings.Current.Edge, atStart) { Width = NotchSettingsGlyph.Extent, Height = NotchSettingsGlyph.Extent,
            IsHitTestVisible = false, LayoutTransform = new ScaleTransform(scale, scale) };
        var gear = Control("", "Open Settings", () => openSettings(null));
        gear.Style = (Style)FindResource("NotchSettingsButton");
        gear.ToolTip = null;
        gear.Width = gear.Height = NotchMetrics.OrbHotZone * scale; gear.Margin = new(0); gear.Content = glyph;
        // The glyph (arc included) is larger than the hit zone. WPF stops centring a child that overflows
        // and pins it to the top-left, which put the gear off the rail's centre; negative margins re-centre it.
        glyph.Margin = new(-Math.Max(0, NotchSettingsGlyph.Extent - NotchMetrics.OrbHotZone) * scale / 2);
        // A one-alpha hit surface keeps the resting arc's center reachable on a layered
        // native window; fully transparent pixels otherwise pass through to the desktop.
        gear.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        gear.FocusVisualStyle = RailFocusVisual((gear.Width - diameter) / 2);
        AutomationProperties.SetAutomationId(gear, "notch.settings");
        PlaceControl(canvas, gear, gearCenter); settingsControl = gear; settingsGlyph = glyph;
        var accounts = Control("\uE895", "Switch account", OpenAccounts);
        accounts.Width = accounts.Height = diameter; accounts.Margin = new(0); accounts.Visibility = Visibility.Hidden;
        // Centred on the glyph's ink, not its line box: icon fonts sit low and off-centre in their em box.
        accounts.Content = new CenteredGlyph("\uE895", NotchMetrics.OrbGlyph * 0.7 * scale) { Width = diameter, Height = diameter };
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
    /// <summary>Keyboard-only focus ring on the rail's circle, not the larger hit region.</summary>
    private static Style RailFocusVisual(double inset)
    {
        var ring = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        ring.SetValue(System.Windows.Shapes.Shape.StrokeProperty, new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)));
        ring.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1d);
        ring.SetValue(MarginProperty, new Thickness(Math.Max(0, inset)));
        var style = new Style(typeof(System.Windows.Controls.Control));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, new ControlTemplate(typeof(System.Windows.Controls.Control)) { VisualTree = ring }));
        style.Seal(); return style;
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
        // macOS: spring(response 0.36, damping 0.7) for the orb's arc-to-gear morph.
        if (settingsGlyph is not null) Motion.To(settingsGlyph, NotchSettingsGlyph.RevealProperty, show ? 1 : 0, 0.72, Motion.Spring(0.7), enabled: Animates);
        if (controlRail is not null)
        {
            if (show && controlRail.Visibility != Visibility.Visible) { controlRail.Opacity = 0; controlRail.Visibility = Visibility.Visible; }
            var rail = controlRail; var target = show ? railOpen : railRest;
            Motion.To(rail, OpacityProperty, show ? 1 : 0, NotchMotion.Crossfade,
                completed: () => { if (!show && revision == controlsRevision) rail.Visibility = Visibility.Hidden; }, enabled: Animates);
            // macOS: the capsule lengthens with spring(response 0.32, damping 0.86) as the account control appears.
            if (!target.IsEmpty)
            {
                var delay = show ? 0.08 : 0;
                Motion.To(rail, Canvas.LeftProperty, target.X, 0.64, Motion.Spring(0.86), delay, enabled: Animates);
                Motion.To(rail, Canvas.TopProperty, target.Y, 0.64, Motion.Spring(0.86), delay, enabled: Animates);
                Motion.To(rail, WidthProperty, target.Width, 0.64, Motion.Spring(0.86), delay, enabled: Animates);
                Motion.To(rail, HeightProperty, target.Height, 0.64, Motion.Spring(0.86), delay, enabled: Animates);
            }
        }
        if (accountControl is not null)
        {
            var account = accountControl;
            var shift = 24 * NotchMetrics.Unit * renderScale;
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
        // The spring may overshoot; opacity stays within range while scale and turn carry the bounce.
        var progress = (double)GetValue(RevealProperty);
        var reveal = Math.Clamp(progress, 0, 1);
        if (reveal > 0)
        {
            // As macOS: the gear grows from half size and turns in from -60 degrees.
            drawing.PushOpacity(reveal);
            drawing.PushTransform(new RotateTransform(-60 * (1 - progress), center.X, center.Y));
            drawing.PushTransform(new ScaleTransform(0.5 + 0.5 * progress, 0.5 + 0.5 * progress, center.X, center.Y));
            CenteredGlyph.Draw(drawing, "\uE713", NotchMetrics.OrbGlyph * 0.8, Brushes.White, center, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.Pop(); drawing.Pop(); drawing.Pop();
        }
        // The resting arc shrinks slightly as it fades, like the macOS orb.
        drawing.PushOpacity(1 - reveal);
        drawing.PushTransform(new ScaleTransform(1 - 0.14 * reveal, 1 - 0.14 * reveal, center.X, center.Y));
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
        drawing.Pop(); drawing.Pop();
    }
}

/// <summary>An icon-font glyph centred on its ink. Text layout centres the line box, which leaves Segoe icons visibly low.</summary>
internal sealed class CenteredGlyph(string glyph, double size) : FrameworkElement
{
    private static readonly Typeface Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    protected override void OnRender(DrawingContext drawing) =>
        Draw(drawing, glyph, size, Brushes.White, new Point(ActualWidth / 2, ActualHeight / 2), VisualTreeHelper.GetDpi(this).PixelsPerDip);
    internal static void Draw(DrawingContext drawing, string glyph, double size, Brush brush, Point center, double pixelsPerDip)
    {
        var text = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Icons, size, brush, pixelsPerDip);
        var ink = text.BuildGeometry(new Point(0, 0)).Bounds;
        if (ink.IsEmpty) { drawing.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2)); return; }
        drawing.DrawText(text, new Point(center.X - ink.X - ink.Width / 2, center.Y - ink.Y - ink.Height / 2));
    }
}
