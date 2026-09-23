using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task CheckMotion(DashboardWindow dashboard, NotchWindow notch, CodeRim.Windows.ViewModels.DashboardStore store, AppSettingsStore settings, string directory)
    {
        var saved = settings.Current;
        var originalAnimations = 0;
        Require(ReadClientAreaAnimation(0x1042, 0, ref originalAnimations, 0), "Could not read the desktop animation policy");
        var samples = new List<object>(); var checks = new List<string>();
        Window? fixture = null;
        try
        {
            // This path is only invoked by the explicitly requested, isolated synthetic smoke run.
            // Enable the disposable desktop session's animation policy, then restore it in finally.
            Require(SetClientAreaAnimation(0x1043, 0, new IntPtr(1), 2), "Could not enable animations in the native smoke desktop");
            var nativeEnabled = 0;
            Require(ReadClientAreaAnimation(0x1042, 0, ref nativeEnabled, 0) && nativeEnabled != 0, "Native animation policy did not enable");
            await MotionFrame(); Motion.RefreshPolicy();
            settings.Save(saved with { ReduceMotion = false, EnabledProviders = ["codex", "claude"], Visibility = NotchVisibility.OnHover });
            await MotionUntil(() => Motion.Enabled, "Native desktop animation policy stayed disabled");
            foreach (var edge in Enum.GetValues<NotchEdge>())
            {
                settings.Save(settings.Current with { Edge = edge }); await MotionFrame();
                notch.Peek(); await MotionUntil(() => notch.FoldProgress is > 0 and < 0.98, "Unfold skipped intermediate geometry: " + edge);
                for (var i = 0; i < 6; i++)
                {
                    samples.Add(new { kind = "unfold", edge = edge.ToString(), frame = i, progress = notch.FoldProgress });
                    Capture(notch, Path.Combine(directory, $"windows-motion-unfold-{edge}-{i:D2}.png"));
                    await Task.Delay(90); await MotionFrame();
                }
                await MotionUntil(() => notch.FoldProgress >= 0.999, "Unfold did not settle");
                notch.SetExpanded(false); await Task.Delay(100); await MotionFrame();
                var reversing = notch.FoldProgress;
                Require(reversing is > 0 and < 1, "Fold skipped intermediate geometry");
                notch.SetExpanded(true);
                Require(Math.Abs(notch.FoldProgress - reversing) < 0.1, "Reversing fold snapped geometry");
                await MotionUntil(() => notch.FoldProgress >= 0.999, "Reversed unfold did not settle");
                notch.SetExpanded(false);
                await MotionUntil(() => !notch.Expanded && notch.FoldProgress == 0, "Fold did not finish");
                var vertical = edge is NotchEdge.Left or NotchEdge.Right;
                var expectedWidth = (vertical ? NotchMetrics.PillDepth : NotchMetrics.PillLength) * settings.Current.Scale;
                var expectedHeight = (vertical ? NotchMetrics.PillLength : NotchMetrics.PillDepth) * settings.Current.Scale;
                var dpi = VisualTreeHelper.GetDpi(notch);
                Require(WindowBounds(new System.Windows.Interop.WindowInteropHelper(notch).Handle, out var bounds), "Folded HWND has no bounds");
                Require(Math.Abs(bounds.Right - bounds.Left - Math.Ceiling(expectedWidth * dpi.DpiScaleX)) <= 1
                    && Math.Abs(bounds.Bottom - bounds.Top - Math.Ceiling(expectedHeight * dpi.DpiScaleY)) <= 1,
                    "Folded native hit bounds remained expanded");
            }
            checks.Add("Four-edge spring geometry has intermediate frames, reverses without snapping and shrinks native hit bounds");
            settings.Save(settings.Current with { Edge = NotchEdge.Right, Visibility = NotchVisibility.AlwaysShow }); await MotionFrame();
            var gear = Descendants<System.Windows.Controls.Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.settings");
            gear.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent }); await MotionFrame();
            var account = Descendants<System.Windows.Controls.Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.switchAccount");
            Require(account.IsVisible && account.Opacity < 1, "Account control skipped its delayed entrance");
            await MotionUntil(() => account.Opacity >= 0.999, "Account entrance did not settle");
            checks.Add("Settings reveal retains the account control's delayed fade/scale entrance");
            notch.OpenProvider("codex"); await Task.Delay(200); await MotionFrame();
            notch.OpenProvider("claude"); await MotionFrame();
            Require(notch.PopupIsOpen && notch.PopupContent is not null, "Provider change lost popup");
            var popupPositions = new List<double>();
            for (var frame = 0; frame < 6; frame++)
            {
                var child = notch.PopupContent!;
                var y = child.PointToScreen(new Point(0, child.ActualHeight / 2)).Y;
                popupPositions.Add(y); samples.Add(new { kind = "popup", frame, y });
                RequirePopupClearOfNotch(notch, "animated provider transition");
                Capture(child, Path.Combine(directory, $"windows-motion-popup-{frame:D2}.png"));
                await Task.Delay(75); await MotionFrame();
            }
            Require(popupPositions.Distinct().Count() > 1, "Provider tooltip skipped its position transition");
            checks.Add("Provider tooltip moves through native intermediate positions without covering the notch");

            dashboard.Navigate("notch"); await Idle();
            var realToggle = Descendants<CheckBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Show edge notch");
            realToggle.IsChecked = false;
            await MotionUntil(() => Motion.GetToggleOffset(realToggle) is > 0 and < 16, "Notch setting toggle skipped its slide");
            Require(realToggle.IsLoaded && !notch.IsVisible, "Notch visibility recreated the settings toggle or failed to hide");
            realToggle.IsChecked = true;
            await MotionUntil(() => Motion.GetToggleOffset(realToggle) == 16, "Notch toggle did not settle after reversal");
            Require(notch.IsVisible, "Notch toggle failed to restore visibility");
            settings.Save(settings.Current with { ShowRemaining = true, RingColor = RingColorMode.Gradient, AnimateGradient = true });
            await MotionFrame();
            var previews = Descendants<ProviderRing>(dashboard).ToArray();
            Require(previews.Length == 3 && previews.All(x => x.Settings.ShowRemaining && x.Settings.AnimateGradient && x.ClockRunning), "Settings previews kept stale appearance or motion settings");
            checks.Add("Actual Notch settings toggle reverses without view replacement; previews update immediately");

            dashboard.Navigate("usage"); await Idle();
            var usage = Descendants<UsagePane>(dashboard).Single();
            usage.SelectProvider("codex"); usage.HandleShortcut(Key.D1, ModifierKeys.Control); await Idle();
            var savedUsage = store.Usage["codex"];
            try
            {
                var metric = Descendants<AnimatedMetric>(usage).Single(x => x.FontSize == 42);
                var initial = metric.DisplayedValue;
                store.Usage["codex"] = savedUsage with { Today = new TokenUsage(initial + 100000, 0, 0) };
                usage.RefreshReadings(); usage.RefreshReadings();
                Require(ReferenceEquals(metric, Descendants<AnimatedMetric>(usage).Single(x => x.FontSize == 42)), "Duplicate refresh replaced the animating total");
                await MotionUntil(() => metric.DisplayedValue > initial && metric.DisplayedValue < initial + 100000, "Duplicate refresh jumped to final total");
                var intermediate = metric.DisplayedValue;
                store.Usage["codex"] = savedUsage with { Today = new TokenUsage(initial + 200000, 0, 0) };
                usage.RefreshReadings();
                Require(Math.Abs(metric.DisplayedValue - intermediate) < 1000, "Retargeted total jumped from its displayed value");
                await MotionUntil(() => metric.DisplayedValue == initial + 200000, "Total failed to settle exactly");
            }
            finally { store.Usage["codex"] = savedUsage; usage.RefreshReadings(); }
            checks.Add("Duplicate store notifications preserve numeric motion; new totals retarget from the displayed value");

            ProviderReading Reading(double percent) => new("codex", ReadingState.Ready, [new("weekly", "Weekly", percent)], DateTimeOffset.Now);
            var ring = new ProviderRing { ProviderId = "codex", Settings = settings.Current with { ShowRemaining = false, RingColor = RingColorMode.Usage }, Reading = Reading(10) };
            var toggle = new CheckBox { Content = "Synthetic motion toggle", IsChecked = false };
            var content = new StackPanel { Margin = new Thickness(20), Background = Brushes.Black };
            content.Children.Add(ring); content.Children.Add(toggle);
            fixture = new Window { Content = content, Width = 320, Height = 200, ShowActivated = false, Topmost = true, Title = "Synthetic motion verification" };
            fixture.Show(); await MotionFrame();
            ring.Reading = Reading(90);
            await MotionUntil(() => ring.Sweep is > 0.1 and < 0.89, "Reading jumps directly to the new value");
            for (var i = 0; i < 5; i++)
            {
                samples.Add(new { kind = "reading", frame = i, sweep = ring.Sweep });
                Capture(content, Path.Combine(directory, $"windows-motion-reading-{i:D2}.png"));
                await Task.Delay(120); await MotionFrame();
            }
            ring.Reading = Reading(0);
            var previous = ring.Sweep;
            for (var i = 0; i < 9; i++)
            {
                await Task.Delay(110); await MotionFrame();
                Require(ring.Sweep <= previous + 0.0001, "Reset arc reversed direction"); previous = ring.Sweep;
                samples.Add(new { kind = "reset", frame = i, sweep = ring.Sweep });
                Capture(content, Path.Combine(directory, $"windows-motion-reset-{i:D2}.png"));
            }
            Require(ring.Sweep == 0, "Reset left a visible endpoint");
            checks.Add("Reading sweep interpolates; reset retracts monotonically to an empty track");
            ring.Reading = Reading(60); ring.Refreshing = true;
            await MotionUntil(() => ring.RefreshRotation is > 0 and < 360, "Refresh reading did not rotate");
            ring.Refreshing = false; await Task.Delay(100); ring.Refreshing = true;
            await MotionUntil(() => Math.Abs(ring.RefreshRotation % 360) < 0.0001, "Rapid refresh did not end at twelve o'clock");
            var stoppedRotation = ring.RefreshRotation; await Task.Delay(120); await MotionFrame();
            Require(ring.RefreshRotation == stoppedRotation, "Refresh repeats indefinitely");
            ring.Refreshing = false;
            checks.Add("Finite refresh rotation stops at a complete turn even after rapid retrigger");
            ring.Active = true; await MotionFrame(); Require(ring.ClockRunning, "Working arc has no render clock");
            ring.Waiting = true; await MotionFrame(); Capture(content, Path.Combine(directory, "windows-motion-waiting.png"));
            fixture.Hide(); await MotionFrame(); Require(!ring.ClockRunning, "Hidden ring retained its render subscription");
            fixture.Show(); await MotionFrame(); Require(ring.ClockRunning, "Visible activity did not resume");
            checks.Add("Working/waiting animation pauses while hidden and resumes on visibility");
            Require(SetClientAreaAnimation(0x1043, 0, IntPtr.Zero, 2), "Could not disable the native animation policy");
            Motion.RefreshPolicy(); await MotionFrame();
            Require(!Motion.Enabled && !ring.ClockRunning, "Disabled OS animation policy was not applied");
            Require(SetClientAreaAnimation(0x1043, 0, new IntPtr(1), 2), "Could not restore the native animation policy");
            Motion.RefreshPolicy(); await MotionFrame();
            Require(Motion.Enabled && ring.ClockRunning, "Enabled OS animation policy was not applied");
            checks.Add("Live native OS animation preference disables and restores motion without stale WPF cache");
            toggle.IsChecked = true;
            await MotionUntil(() => Motion.GetToggleOffset(toggle) is > 0 and < 16, "Toggle thumb skipped its transition");
            settings.Save(settings.Current with { ReduceMotion = true }); await MotionFrame();
            Require(Motion.GetToggleOffset(toggle) == 16 && !ring.ClockRunning, "Reduce Motion did not settle active animations");
            ring.Reading = Reading(37); Require(Math.Abs(ring.Sweep - 0.37) < 0.0001, "Reduced motion reading was delayed");
            toggle.IsChecked = false; Require(Motion.GetToggleOffset(toggle) == 0, "Reduced motion toggle was delayed");
            checks.Add("Reduced motion immediately settles in-flight readings, toggles and activity clocks");
            fixture.Close(); fixture = null; await MotionFrame(); Require(!ring.ClockRunning, "Closed ring retained render subscription");
            File.WriteAllText(Path.Combine(directory, "windows-motion.json"), JsonSerializer.Serialize(new { kind = "Native WPF animation frames", systemAnimationsBefore = originalAnimations, checks, samples }, JsonOptions));
        }
        finally
        {
            fixture?.Close(); settings.Save(saved);
            SetClientAreaAnimation(0x1043, 0, new IntPtr(originalAnimations), 2);
            await MotionFrame(); Motion.RefreshPolicy();
        }
    }
    private static async Task MotionFrame() => await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    private static async Task MotionUntil(Func<bool> condition, string failure)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        { await Task.Delay(10); await MotionFrame(); }
        Require(condition(), failure);
    }
#pragma warning disable SYSLIB1054
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetClientAreaAnimation(uint action, uint parameter, IntPtr value, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadClientAreaAnimation(uint action, uint parameter, ref int value, uint flags);
#pragma warning restore SYSLIB1054
}
