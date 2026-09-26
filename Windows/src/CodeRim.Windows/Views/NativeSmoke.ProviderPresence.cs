using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task ProviderPresenceRegression(NotchWindow notch, DashboardStore store, AppSettingsStore settings, string directory)
    {
        var before = settings.Current;
        string[] ids = ["codex", "cursor", "gemini", "ollama-local"];
        var old = ids.ToDictionary(id => id, id => store.Readings.GetValueOrDefault(id));
        var checks = new List<string>(); Exception? failure = null; var cleanup = new List<Exception>();
        ProviderReading Ready(string id) => new(id, ReadingState.Ready, [new("quota", "Quota", 25)], DateTimeOffset.Now);
        string[] Visible() => Descendants<ProviderRing>(notch).Select(ring => ring.ProviderId).ToArray();
        try
        {
            foreach (var id in ids) store.Readings.Remove(id);
            store.Readings["codex"] = Ready("codex");
            settings.Save(before with { EnabledProviders = ids, Edge = NotchEdge.Left, Scale = 1, Visibility = NotchVisibility.AlwaysShow });
            await Idle();
            Require(Visible().SequenceEqual(["codex"], StringComparer.Ordinal), "Absent borrowed providers created empty notch cells.");
            checks.Add("Absent borrowed providers have no initial empty ring");
            foreach (var id in ids.Skip(1)) store.Readings[id] = Ready(id);
            notch.RefreshReadings(); await Idle();
            Require(Visible().SequenceEqual(ids, StringComparer.Ordinal), "Successful Account-null providers did not appear in configured order.");
            checks.Add("Success restores configured order including Account-null Antigravity and local Ollama");
            var viewport = Descendants<ScrollViewer>(notch).Single();
            var firstRing = Descendants<ProviderRing>(notch).First();
            store.Readings["cursor"] = Ready("cursor") with { Windows = [new("quota", "Quota", 30)] };
            notch.RefreshReadings(); await Idle();
            Require(ReferenceEquals(viewport, Descendants<ScrollViewer>(notch).Single())
                && ReferenceEquals(firstRing, Descendants<ProviderRing>(notch).First()), "Unchanged membership rebuilt the live notch viewport.");
            checks.Add("Ordinary reading updates preserve ring and viewport objects");
            notch.OpenProvider("cursor"); await Idle(); Require(notch.PopupIsOpen, "Cursor failure fixture did not open its popup.");
            foreach (var id in ids.Skip(1)) store.Readings[id] = ReadingRetention.Merge(new(id, ReadingState.Error, [],
                Account: new("detected@example.invalid", "Local")), store.Readings[id]);
            notch.RefreshReadings(); await Idle();
            Require(Visible().SequenceEqual(["codex"], StringComparer.Ordinal) && !notch.PopupIsOpen
                && settings.Current.EnabledProviders.SequenceEqual(ids, StringComparer.Ordinal), "Failure kept a rejected ring/popup or disabled the provider.");
            Capture(notch, Path.Combine(directory, "windows-native-presence-failed.png"));
            checks.Add("Failure removes quota and cells even with a detected account and closes the removed popup");
            store.Readings["cursor"] = Ready("cursor"); notch.RefreshReadings(); await Idle();
            Require(Visible().SequenceEqual(["codex", "cursor"], StringComparer.Ordinal), "Recovered provider did not return in its saved order.");
            notch.OpenProvider("codex"); await Idle();
            var existingPopup = notch.PopupContent;
            store.Readings["gemini"] = Ready("gemini"); notch.RefreshReadings(); await Idle();
            Require(notch.PopupIsOpen && ReferenceEquals(existingPopup, notch.PopupContent), "An unrelated membership change replaced a surviving provider popup.");
            checks.Add("Recovery keeps configuration and preserves a surviving provider popup");
            var accounts = Descendants<Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.switchAccount");
            accounts.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Require(notch.AccountMenuIsOpen, "Presence fixture did not open the account menu.");
            existingPopup = notch.PopupContent;
            store.Readings["cursor"] = new("cursor", ReadingState.Error, []); notch.RefreshReadings(); await Idle();
            Require(notch.AccountMenuIsOpen && ReferenceEquals(existingPopup, notch.PopupContent), "Unrelated provider failure replaced or closed the account menu.");
            checks.Add("Account menu remains mounted during provider membership changes");
            store.Readings["cursor"] = Ready("cursor");
            settings.Save(settings.Current with { EnabledProviders = ProviderCatalog.All.Select(x => x.Id).ToArray(), Visibility = NotchVisibility.AlwaysShow }); await Idle();
            viewport = Descendants<ScrollViewer>(notch).Single(); Require(viewport.ScrollableHeight > 200, "Presence scroll fixture has no scrollable list.");
            viewport.ScrollToVerticalOffset(180); await Idle(); var offset = viewport.VerticalOffset;
            Require(Math.Abs(offset - 180) < .1, "Presence scroll fixture did not reach its requested nonzero offset.");
            store.Readings["cursor"] = new("cursor", ReadingState.NeedsAuth, []); notch.RefreshReadings(); await Idle();
            Require(Math.Abs(Descendants<ScrollViewer>(notch).Single().VerticalOffset - offset) < .1,
                "Membership change reset the surviving list scroll position.");
            checks.Add("Scrollable membership changes preserve the existing scroll offset");
            File.WriteAllText(Path.Combine(directory, "windows-native-presence.json"), JsonSerializer.Serialize(new { completed = true,
                fixture = true, physicalInput = false, checks }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            foreach (var pair in old) Restore(() => { if (pair.Value is null) store.Readings.Remove(pair.Key); else store.Readings[pair.Key] = pair.Value; });
            Restore(() => settings.Save(before)); Restore(() => notch.Render());
        }
        if (cleanup.Count > 0) throw new AggregateException("Provider presence fixture cleanup failed.", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
