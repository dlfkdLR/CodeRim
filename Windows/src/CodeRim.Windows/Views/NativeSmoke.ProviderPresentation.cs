using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static string ProviderHeaderStatus(DashboardWindow window) => Descendants<TextBlock>(window)
        .Single(x => AutomationProperties.GetAutomationId(x) == "provider.status").Text;

    private static TextBlock ProviderListStatus(DashboardWindow window, string id) => Descendants<TextBlock>(window)
        .Single(x => AutomationProperties.GetAutomationId(x) == "provider-list." + id);

    private static void RequireFailedProviderListRow(DashboardWindow window, ProviderReading failure)
    {
        var label = ProviderListStatus(window, failure.Id);
        Require(!label.Text.Contains('%')
            && (string.IsNullOrEmpty(failure.Message) || !label.Text.Contains(failure.Message, StringComparison.Ordinal))
            && !Descendants<Button>(window).Single(x => AutomationProperties.GetAutomationId(x) == "settings.providers.alerts." + failure.Id).IsVisible,
            failure.Id + " failed provider list retained quota, raw error copy or the connected bell.");
    }

    private static void RequireAbsentProviderPresentation(DashboardWindow window, ProviderReading failure)
    {
        Require(ProviderHeaderStatus(window) == "Not connected" && !Descendants<ProgressBar>(window).Any()
            && !Descendants<Button>(window).Any(x => AutomationProperties.GetAutomationId(x) == "provider.alerts" && x.IsVisible)
            && (string.IsNullOrEmpty(failure.Message) || !Descendants<TextBlock>(window).Any(x => x.IsVisible && x.Text == failure.Message)),
            failure.Id + " failed Settings differs from the Mac absent snapshot (status, quota, bell or repeated error note).");
    }

    private static async Task ProviderFailurePresentationRegression(DashboardWindow dashboard, NotchWindow notch,
        DashboardStore store, AppSettingsStore settings, string directory)
    {
        string[] ids = ["copilot", "cursor", "grok", "commandcode", "opencode", "glm", "ollama", "gemini", "ollama-local"];
        var before = settings.Current;
        var old = ids.Append("codebuff").ToDictionary(id => id, id => store.Readings.GetValueOrDefault(id));
        Exception? failure = null; var cleanup = new List<Exception>(); var checks = new List<string>();
        var observations = new List<object>();
        bool RingPresent(string id) => Descendants<ProviderRing>(notch).Any(x => x.ProviderId == id);
        ProviderReading Ready(string id) => new(id, ReadingState.Ready, [new("quota", "Quota", 25)], DateTimeOffset.Now,
            Account: new("presentation@example.invalid", "Fixture"));
        try
        {
            foreach (var id in ids) store.Readings.Remove(id);
            settings.Save(before with { EnabledProviders = [..ids, "codebuff"], Visibility = NotchVisibility.AlwaysShow });
            foreach (var id in ids)
            {
                dashboard.Navigate(id); await Idle();
                Require(ProviderHeaderStatus(dashboard) == "Not connected" && !RingPresent(id)
                    && !Descendants<ProgressBar>(dashboard).Any(), id + " absent initial Settings or notch differs from Mac.");
                foreach (var state in new[] { ReadingState.Error, ReadingState.NeedsAuth, ReadingState.Unavailable, ReadingState.Unsupported, ReadingState.Disabled })
                {
                    store.Readings[id] = Ready(id);
                    dashboard.UpdateProviderReading(id); notch.RefreshReadings(); await Idle();
                    Require(ProviderHeaderStatus(dashboard) == "Connected" && RingPresent(id)
                        && Descendants<ProgressBar>(dashboard).Any(), id + " recovery did not restore quota and ring.");
                    notch.OpenProvider(id); await Idle(); Require(notch.PopupIsOpen, id + " failure fixture did not open a real popup.");
                    var failed = new ProviderReading(id, state, [], Message: "Fixture " + id + " " + state,
                        Account: new("presentation@example.invalid", "Fixture"));
                    store.Readings[id] = ReadingRetention.Merge(failed, store.Readings[id]);
                    dashboard.UpdateProviderReading(id); notch.RefreshReadings(); await Idle();
                    RequireAbsentProviderPresentation(dashboard, failed);
                    Require(ReferenceEquals(store.Readings[id], failed) && !RingPresent(id) && !notch.PopupIsOpen
                        && settings.Current.EnabledProviders.Contains(id, StringComparer.Ordinal), id + " Settings projection changed failure storage or resurrected its ring/popup.");
                    observations.Add(new { id, phase = state.ToString(), status = ProviderHeaderStatus(dashboard), ring = RingPresent(id),
                        rawFailurePreserved = ReferenceEquals(store.Readings[id], failed), popup = notch.PopupIsOpen });
                }
                dashboard.Navigate("providers"); await Idle();
                RequireFailedProviderListRow(dashboard, store.Readings[id]);
                dashboard.Navigate(id); await Idle();
                // A remembered reading is still presented; failure projection
                // must not mistake ordinary aging for a failed fetch.
                store.Readings[id] = Ready(id) with { State = ReadingState.Stale, UpdatedAt = DateTimeOffset.Now.AddMinutes(-10) };
                dashboard.UpdateProviderReading(id); notch.RefreshReadings(); await Idle();
                Require(ProviderHeaderStatus(dashboard).StartsWith("Last read ", StringComparison.Ordinal)
                    && Descendants<ProgressBar>(dashboard).Any() && RingPresent(id), id + " stale restored quota was hidden as a failure.");
                checks.Add(id + " initial, success, five failure phases, popup removal, recovery and stale archive remain consistent");
            }
            var unaffected = new ProviderReading("codebuff", ReadingState.Error, [], Message: "Fixture Codebuff diagnostic");
            store.Readings["codebuff"] = unaffected; dashboard.Navigate("codebuff"); await Idle();
            Require(ProviderHeaderStatus(dashboard) == unaffected.Message && Descendants<TextBlock>(dashboard)
                .Count(x => x.IsVisible && x.Text == unaffected.Message) == 2 && ReferenceEquals(store.Readings["codebuff"], unaffected),
                "Absent-provider Settings policy suppressed another provider's existing error information.");
            checks.Add("Other providers retain their existing failure presentation and stored reading");
            File.WriteAllText(Path.Combine(directory, "windows-provider-failure-presentation.json"), JsonSerializer.Serialize(new
                { completed = true, fixture = true, realAccount = false, checks, observations }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            foreach (var pair in old) Restore(() => { if (pair.Value is null) store.Readings.Remove(pair.Key); else store.Readings[pair.Key] = pair.Value; });
            Restore(() => settings.Save(before)); Restore(() => dashboard.Navigate("usage")); Restore(() => notch.Render());
        }
        if (cleanup.Count > 0) throw new AggregateException("Provider failure presentation cleanup failed.", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
