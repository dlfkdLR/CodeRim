using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task AccountLimitsRegression(DashboardWindow dashboard, DashboardStore store, AppSettingsStore settings, string directory)
    {
        var before = settings.Current; var previous = store.Readings["codex"];
        var previousClaude = store.Readings.GetValueOrDefault("claude");
        var dark = SettingsTheme.IsDark; var contrast = SettingsTheme.IsHighContrast;
        var size = new Size(dashboard.Width, dashboard.Height); UsagePane? pane = null; var wasLimits = false;
        Expander? originalAbout = null; var aboutWasExpanded = false;
        Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            settings.Save(before with { EnabledProviders = ["codex", "claude"], AccountLimitsEnabled = true, ResetCreditsEnabled = true, AdditionalLimitsEnabled = true });
            var now = DateTimeOffset.Now;
            using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { rateLimitsByLimitId = new {
                codex = new { limitName = "Codex", primary = new { usedPercent = 75, windowDurationMins = 300, resetsAt = now.AddMinutes(150).ToUnixTimeSeconds() },
                    secondary = new { usedPercent = 50, windowDurationMins = 10080, resetsAt = now.AddMinutes(5040).ToUnixTimeSeconds() } },
                codex_bengalfox = new { limitName = "GPT-5.3-Codex-Spark", primary = new { usedPercent = 98.4, windowDurationMins = 300, resetsAt = now.AddMinutes(150).ToUnixTimeSeconds() } } },
                rateLimitResetCredits = new { availableCount = 2 } }));
            store.Readings["codex"] = new("codex", ReadingState.Ready, ProviderParsers.Codex(payload.RootElement), now, Plan: "pro");
            dashboard.Navigate("usage"); await Idle(); pane = Descendants<UsagePane>(dashboard).Single(); wasLimits = pane.ShowsLimits;
            pane.SelectProvider("codex"); pane.HandleShortcut(Key.D2, ModifierKeys.Control); await Idle();
            var bars = Descendants<ProgressBar>(pane).ToArray();
            Require(bars.Length == 3 && AutomationProperties.GetAutomationId(bars[0]) == "usage.limit.remaining.codex.secondary"
                && bars[0].Value == 50 && bars[1].Value == 25 && Math.Abs(bars[2].Value - 1.6) < 0.0001,
                "Account quota order or remaining progress differs from the reference.");
            var track = (FrameworkElement)bars[1].Template.FindName("PART_Track", bars[1]);
            var fill = (FrameworkElement)bars[1].Template.FindName("PART_Indicator", bars[1]);
            Require(track.ActualWidth > 0 && Math.Abs(fill.ActualWidth / track.ActualWidth - 0.25) < 0.01, "Account quota bar rendered used instead of remaining.");
            Require(Descendants<TextBlock>(pane).Any(x => x.Text.StartsWith("25% above even pace", StringComparison.Ordinal))
                && Descendants<TextBlock>(pane).Any(x => x.Text == "GPT-5.3-Codex-Spark"), "Pace or reported limit name did not reach the mounted card.");
            var about = originalAbout = Descendants<Expander>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.limits.about");
            aboutWasExpanded = about.IsExpanded;
            var disclosure = Descendants<ToggleButton>(about).Single();
            var disclosurePeer = (System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(disclosure);
            if (about.IsExpanded) disclosurePeer.Toggle();
            disclosurePeer.Toggle(); await Idle();
            Require(about.IsExpanded && Descendants<TextBlock>(about).Any(x => x.Text.Contains("read-only", StringComparison.Ordinal)), "Limits disclosure did not open its read-only explanation.");
            pane.RefreshLimitClock(now.AddMinutes(6));
            Require(Descendants<TextBlock>(pane).Any(x => x.Text == "Last known · updated 6 min ago")
                && Descendants<TextBlock>(pane).Where(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.limit.pace.", StringComparison.Ordinal)).All(x => !x.IsVisible),
                "Clock-only updates did not age the snapshot or remove stale pace estimates.");
            Require(ReferenceEquals(disclosure, Descendants<ToggleButton>(about).Single()), "Clock update replaced the disclosure control.");
            Keyboard.Focus(disclosure); Require(disclosure.IsKeyboardFocusWithin, "Disclosure fixture did not acquire keyboard focus.");
            store.Readings["codex"] = store.Readings["codex"] with { UpdatedAt = now.AddSeconds(1) };
            pane.RefreshReadings(); pane.RefreshLimitClock(now.AddSeconds(2)); await Idle();
            Require(disclosure.IsKeyboardFocusWithin && Descendants<ToggleButton>(pane).Contains(disclosure), "Same-account poll lost disclosure focus on a clock tick.");
            Keyboard.ClearFocus(); await Idle();
            pane.RefreshLimitClock(now);
            dashboard.Width = dashboard.MinWidth; dashboard.Height = dashboard.MinHeight;
            foreach (var theme in new[] { "dark", "light", "contrast" })
            {
                SettingsTheme.Apply(theme == "dark", theme == "contrast"); pane.Update(); await Idle();
                var cards = Descendants<Border>(pane).Where(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.limit.", StringComparison.Ordinal)).ToArray();
                Require(cards.Length == 3 && cards.All(card => card.ActualWidth > 100 && card.TranslatePoint(new Point(card.ActualWidth, 0), dashboard).X <= dashboard.ActualWidth),
                    "Account limit cards clip horizontally at minimum window width.");
                if (theme == "contrast") Require(Descendants<ProgressBar>(pane).All(bar => bar.Foreground is System.Windows.Media.SolidColorBrush brush && brush.Color == SystemColors.HighlightColor),
                    "High contrast failed to recolor account progress.");
                Capture(dashboard, Path.Combine(directory, "windows-account-limits-" + theme + ".png"));
            }
            settings.Save(settings.Current with { AdditionalLimitsEnabled = false }); pane.Update(); await Idle();
            Require(Descendants<ProgressBar>(pane).Count() == 2 && !Descendants<TextBlock>(pane).Any(x => x.Text == "GPT-5.3-Codex-Spark"), "Additional quota preference failed or hid primary quotas.");
            store.Readings["codex"] = new("codex", ReadingState.Ready, [], now); pane.Update(); await Idle();
            Require(Descendants<TextBlock>(pane).Any(x => x.Text == "No account limit windows were returned."), "Empty limits invented quota data.");
            store.Readings["codex"] = new("codex", ReadingState.Loading, []); pane.Update(); await Idle();
            Require(Descendants<FrameworkElement>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.limits.loading"), "Loading limits have no reading state.");
            var spinner = Descendants<LimitActivityIndicator>(pane).Single();
            Require(spinner.IsRunning == Motion.Enabled, "Loading indicator does not respect the active animation policy.");
            store.Readings["codex"] = new("codex", ReadingState.Unavailable, [], Message: "Fixture unavailable"); pane.Update(); await Idle();
            Require(!spinner.IsRunning, "Removed loading indicator retained its animation timer.");
            Require(Descendants<TextBlock>(pane).Any(x => x.Text == "Fixture unavailable"), "Unavailable limits lost their safe error copy.");
            store.Readings["codex"] = new("codex", ReadingState.Ready,
                [new("codex.primary", "5 hours", 20, now.AddMinutes(-1), 300)], now);
            pane.Update(); await Idle(); pane.RefreshLimitClock(now);
            Require(Descendants<TextBlock>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.limits.freshness" && x.Text == "Updated just now"),
                "A reported Codex reset invalidated a fresh server reading.");
            store.Readings["claude"] = new("claude", ReadingState.Ready,
                [new("five_hour", "5 hours", 50, now.AddMinutes(150), 300)], now.AddMinutes(-6));
            pane.SelectProvider("claude"); pane.HandleShortcut(Key.D2, ModifierKeys.Control); await Idle(); pane.RefreshLimitClock(now);
            Require(Descendants<TextBlock>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.limits.freshness" && x.Text == "Updated 6 min ago")
                && Descendants<TextBlock>(pane).Any(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.limit.pace.", StringComparison.Ordinal) && x.IsVisible),
                "Claude limits became stale at the generic five-minute cutoff.");
            Capture(dashboard, Path.Combine(directory, "windows-claude-limits-freshness.png"));
            pane.RefreshLimitClock(now.AddMinutes(10));
            Require(Descendants<TextBlock>(pane).Any(x => x.Text == "Last known · updated 16 min ago")
                && Descendants<TextBlock>(pane).Where(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.limit.pace.", StringComparison.Ordinal)).All(x => !x.IsVisible),
                "Claude limits did not age after their own fifteen-minute cutoff.");
            File.WriteAllText(Path.Combine(directory, "windows-account-limits.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Parser-to-card metadata and primary-weekly ordering", "Rendered remaining progress and retained Pro five-hour Usage data", "Pace and 30-second clock callback without replacing disclosure", "Minimum-width dark/light/high-contrast cards", "Additional quota preference", "Empty/loading/unavailable states", "Codex fresh reset and Claude fifteen-minute freshness policy" } }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => store.Readings["codex"] = previous);
            Restore(() => { if (previousClaude is null) store.Readings.Remove("claude"); else store.Readings["claude"] = previousClaude; });
            Restore(() => pane?.SelectProvider("codex")); Restore(() => settings.Save(before));
            Restore(() => { if (originalAbout is not null) originalAbout.IsExpanded = aboutWasExpanded; });
            Restore(() => pane?.HandleShortcut(wasLimits ? Key.D2 : Key.D1, ModifierKeys.Control));
            Restore(() => { dashboard.Width = size.Width; dashboard.Height = size.Height; });
            Restore(() => SettingsTheme.Apply(dark, contrast)); Restore(() => dashboard.Navigate("notch"));
        }
        if (cleanup.Count > 0) throw new AggregateException("Account limits fixture cleanup failed.", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
