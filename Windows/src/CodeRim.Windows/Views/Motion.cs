using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Views;

/// <summary>Finite, replaceable WPF animations. Base values always hold the final state.</summary>
internal static class Motion
{
    private sealed record Running(DependencyObject Target, DependencyProperty Property, Action? Completed);
    private static readonly Dictionary<(DependencyObject, DependencyProperty), Running> RunningAnimations = [];
    private static bool reduced;
    internal static bool IsAnimating => RunningAnimations.Count != 0;
    internal static bool Enabled => !reduced && SystemParameters.ClientAreaAnimation;
    internal static event Action? PolicyChanged;
    internal static void SetReduced(bool value) { reduced = value; RefreshPolicy(); }
    internal static void RefreshPolicy()
    {
        if (!Enabled)
            foreach (var item in RunningAnimations.Values.ToArray()) Finish(item);
        PolicyChanged?.Invoke();
    }
    internal static IEasingFunction Spring(double damping) => new SpringEase(damping);
    private sealed class SpringEase(double damping) : IEasingFunction
    { public double Ease(double normalizedTime) => NotchMotion.Spring(normalizedTime, damping); }
    internal static IEasingFunction Smooth => new SineEase { EasingMode = EasingMode.EaseInOut };
    internal static void To(DependencyObject target, DependencyProperty property, double to, double seconds,
        IEasingFunction? ease = null, double delay = 0, Action? completed = null, bool? enabled = null)
    {
        var from = (double)target.GetValue(property);
        var key = (target, property);
        RunningAnimations.Remove(key);
        ((IAnimatable)target).BeginAnimation(property, null);
        target.SetValue(property, to);
        if (!(enabled ?? Enabled) || !double.IsFinite(from) || Math.Abs(from - to) < 0.00001)
        { completed?.Invoke(); return; }
        var item = new Running(target, property, completed); RunningAnimations[key] = item;
        // BeginTime alone exposes the final base value before a delayed clock starts.
        // Hold the sampled value with an active key frame instead, so staggered cells never flash.
        AnimationTimeline animation;
        if (delay > 0)
        {
            var frames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(delay))));
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(delay + seconds)), ease ?? Smooth));
            animation = frames;
        }
        else animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        { EasingFunction = ease ?? Smooth, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => Finish(item);
        ((IAnimatable)target).BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private static void Finish(Running item)
    {
        var key = (item.Target, item.Property);
        if (!RunningAnimations.TryGetValue(key, out var current) || !ReferenceEquals(current, item)) return;
        RunningAnimations.Remove(key);
        ((IAnimatable)item.Target).BeginAnimation(item.Property, null);
        item.Completed?.Invoke();
    }
    internal static void Stop(DependencyObject target)
    {
        foreach (var item in RunningAnimations.Values.Where(x => ReferenceEquals(x.Target, target)).ToArray())
        {
            RunningAnimations.Remove((target, item.Property));
            ((IAnimatable)target).BeginAnimation(item.Property, null);
        }
    }
    internal static void Enter(FrameworkElement element, double seconds = NotchMotion.Crossfade)
    {
        if (!Enabled) return;
        element.Opacity = 0;
        To(element, UIElement.OpacityProperty, 1, seconds);
    }

    public static readonly DependencyProperty FeedbackProperty = DependencyProperty.RegisterAttached(
        "Feedback", typeof(bool), typeof(Motion), new PropertyMetadata(false, FeedbackChanged));
    public static bool GetFeedback(DependencyObject target) => (bool)target.GetValue(FeedbackProperty);
    public static void SetFeedback(DependencyObject target, bool value) => target.SetValue(FeedbackProperty, value);
    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached("Hover", typeof(double), typeof(Motion), new PropertyMetadata(0d));
    public static double GetHover(DependencyObject target) => (double)target.GetValue(HoverProperty);
    public static readonly DependencyProperty ToggleOffsetProperty = DependencyProperty.RegisterAttached("ToggleOffset", typeof(double), typeof(Motion), new PropertyMetadata(0d));
    public static double GetToggleOffset(DependencyObject target) => (double)target.GetValue(ToggleOffsetProperty);
    private static void FeedbackChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if ((bool)e.NewValue)
        {
            element.MouseEnter += FeedbackEvent; element.MouseLeave += FeedbackEvent;
            element.PreviewMouseDown += FeedbackEvent; element.PreviewMouseUp += FeedbackEvent;
            element.GotKeyboardFocus += FeedbackEvent; element.LostKeyboardFocus += FeedbackEvent;
            element.Loaded += Initialize; element.Unloaded += Unload;
            if (element is CheckBox box) { box.Checked += Toggle; box.Unchecked += Toggle; }
        }
        else
        {
            element.MouseEnter -= FeedbackEvent; element.MouseLeave -= FeedbackEvent;
            element.PreviewMouseDown -= FeedbackEvent; element.PreviewMouseUp -= FeedbackEvent;
            element.GotKeyboardFocus -= FeedbackEvent; element.LostKeyboardFocus -= FeedbackEvent;
            element.Loaded -= Initialize; element.Unloaded -= Unload;
            if (element is CheckBox box) { box.Checked -= Toggle; box.Unchecked -= Toggle; }
            Stop(element);
        }
    }
    private static void Initialize(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        element.SetValue(HoverProperty, element.IsMouseOver ? 1d : 0d);
        if (element is CheckBox box) box.SetValue(ToggleOffsetProperty, box.IsChecked == true ? 16d : 0d);
    }
    private static void Unload(object sender, RoutedEventArgs e) => Stop((DependencyObject)sender);
    private static void FeedbackEvent(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        To(element, HoverProperty, element.IsMouseOver || element.IsKeyboardFocusWithin ? 1 : 0, NotchMotion.Crossfade);
    }
    private static void Toggle(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        To(box, ToggleOffsetProperty, box.IsChecked == true ? 16 : 0, 0.2, enabled: box.IsLoaded && Enabled);
    }
}
