using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task ProviderRowsRegression(DashboardWindow dashboard, DashboardStore store, AppSettingsStore settings, string directory)
    {
        var before = settings.Current; var codex = store.Readings.GetValueOrDefault("codex"); var copilot = store.Readings.GetValueOrDefault("copilot");
        Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            settings.Save(before with { EnabledProviders = ["codex", "copilot"], AlertsEnabled = true, MutedAlertProviders = [] });
            store.Readings["codex"] = new("codex", ReadingState.Ready, [new("session", "5 hours", 20), new("week", "Weekly", 66)], DateTimeOffset.Now, Plan: "Preview account");
            store.Readings["copilot"] = new("copilot", ReadingState.NeedsAuth, [], Message: "Sign in with GitHub CLI.");
            dashboard.Navigate("providers"); await Idle();
            Button Button(string id) => Descendants<Button>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == id);
            var detail = Descendants<TextBlock>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "provider-list.codex");
            Require(detail.Text.Contains("preview@example.invalid", StringComparison.Ordinal) && detail.Text.Contains("Preview account", StringComparison.Ordinal) && detail.Text.Contains("66% of Weekly", StringComparison.Ordinal), "Provider row lost account, plan or most-used limit.");
            var alert = Button("settings.providers.alerts.codex");
            Require(alert.IsVisible && AutomationProperties.GetName(alert) == "Mute Codex alerts", "Connected provider has no accessible alert bell.");
            alert.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Require(new AppSettingsStore().Current.MutedAlertProviders.Contains("codex", StringComparer.Ordinal) && AutomationProperties.GetName(alert) == "Unmute Codex alerts", "Alert bell did not persist muted state in place.");
            settings.Save(settings.Current with { AlertsEnabled = false }); await Idle();
            Require(!alert.IsEnabled, "Global alert setting did not disable the existing row's bell.");
            Require(!Button("settings.providers.alerts.copilot").IsVisible && Button("settings.providers.primary.copilot").Content as string == "Set Up…", "Disconnected provider retained connected actions.");
            Capture(dashboard, Path.Combine(directory, "windows-provider-rows.png"));
            Button("settings.providers.primary.copilot").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Require(dashboard.Title == "Providers" && Descendants<FrameworkElement>(dashboard).Any(x => AutomationProperties.GetAutomationId(x) == "provider.connection"), "Set Up action failed to open provider connection settings.");
            store.Readings["copilot"] = new("copilot", ReadingState.Stale, [], DateTimeOffset.Now.AddMinutes(-90));
            dashboard.Navigate("copilot"); await Idle();
            Require(Descendants<TextBlock>(dashboard).Any(x => x.Text == "Last read 1 hr 30 min ago"), "Provider detail stale status differs from the reference's relative time.");
            dashboard.Navigate("providers"); await Idle();
            Require(Descendants<TextBlock>(dashboard).Any(x => AutomationProperties.GetAutomationId(x) == "provider-list.copilot" && x.Text == "Last read 1 hr 30 min ago"),
                "Provider row stale status differs from its detail view.");
            Button("settings.providers.remove.copilot").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Require(!settings.Current.EnabledProviders.Contains("copilot", StringComparer.Ordinal) && store.Readings.ContainsKey("copilot"), "Removing a provider did not stop monitoring or destructively cleared its reading.");
            File.WriteAllText(Path.Combine(directory, "windows-provider-rows.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Account/plan/highest-used limit share a stable row", "Bell persists mute state and follows global alerts", "Disconnected providers expose setup and hide alert controls", "Relative stale time agrees across list and detail", "Setup navigation and removal preserve provider state" } }));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => { if (codex is null) store.Readings.Remove("codex"); else store.Readings["codex"] = codex; });
            Restore(() => { if (copilot is null) store.Readings.Remove("copilot"); else store.Readings["copilot"] = copilot; });
            Restore(() => settings.Save(before)); Restore(() => dashboard.Navigate("usage"));
        }
        if (cleanup.Count > 0) throw new AggregateException("Provider row fixture cleanup failed", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
