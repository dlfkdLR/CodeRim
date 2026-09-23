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
    private static async Task ProfileHistoryRegression(AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var before = settings.Current; DashboardWindow? window = null; DashboardStore? localStore = null;
        var unfinished = new List<TaskCompletionSource<ProfileUsageSnapshot>>();
        var tasks = new List<Task>(); var cleanup = new List<Exception>(); Exception? failure = null;
        var now = new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.FromHours(9));
        ProfileCredential Credential(string subject) => new("header." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { sub = subject })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature", "fixture-workspace");
        var current = Credential("a"); var busy = false; var unavailable = false; var calls = 0;
        ProfileUsageSnapshot Reading(ProfileCredential owner, long total) => new(99999999, total / 10, total / 2, total, new DateOnly(2026, 9, 22), now, owner.AccountKey);
        Func<CancellationToken, Task<ProfileUsageSnapshot>> response = _ => Task.FromResult(Reading(current, 1000000));
        settings.Save(before with { ProfileSyncEnabled = false, EnabledProviders = ["codex", "claude"], NumberStyle = TokenNumberStyle.Detailed, ReduceMotion = true });
        using var profile = new ProfileUsageStore(settings, true, () => unavailable ? throw new ProfileCredentialException() : current,
            (_, _, token) => { calls++; return response(token); }, () => busy, () => now);
        TaskCompletionSource<ProfileUsageSnapshot> Pause()
        {
            var completion = new TaskCompletionSource<ProfileUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously); unfinished.Add(completion);
            response = _ => completion.Task; return completion;
        }
        try
        {
            localStore = new DashboardStore(settings, vault, true, profileHistory: profile); await localStore.RefreshAsync();
            window = new DashboardWindow(localStore, settings, vault); window.Show(); await Idle();
            var pane = Descendants<UsagePane>(window).Single();
            Require(calls == 0 && profile.Snapshot is null, "Disabled profile history fetched or retained account totals.");
            Button Button(string id) => Descendants<Button>(window).Single(x => AutomationProperties.GetAutomationId(x) == id);
            Button("history.account.enable").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await profile.RefreshAsync(); await Idle();
            Require(profile.Status == ProfileUsageStatus.Ready && calls == 1 && new AppSettingsStore().Current.ProfileSyncEnabled, "Account history enable did not persist or fetch exactly once.");
            Require(Descendants<TextBlock>(pane).Any(x => x.Text == "ChatGPT account") && Descendants<TextBlock>(pane).Any(x => x.Text == "Lifetime"), "History lacks account scope or Lifetime label.");
            Require(Descendants<AnimatedMetric>(pane).Single(x => x.FontSize == 42).DisplayedValue != 99999999, "Server's last reported day replaced local Today.");
            Button("usage.history.all-time").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Require(Descendants<AnimatedMetric>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.period.total").DisplayedValue == 1000000,
                "Account detail added local tokens to server lifetime.");
            Require(Descendants<TextBlock>(pane).Any(x => x.Text == "This PC local history"), "Account detail lost its separate local breakdown.");
            Capture(window, Path.Combine(directory, "windows-account-history-detail.png"));
            Button("usage.navigation.back").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Button("history.local.details").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Require(Descendants<AnimatedMetric>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.period.total").DisplayedValue == localStore.Usage["codex"].AllTime.TotalTokens,
                "Local history link displayed account totals.");
            Button("usage.navigation.back").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Capture(window, Path.Combine(directory, "windows-account-history-overview.png"));

            var ownerA = current; var oldCompletion = Pause(); var old = profile.RefreshAsync(true); tasks.Add(old); await Idle();
            current = Credential("b"); response = _ => Task.FromResult(Reading(current, 2000000));
            await profile.RefreshAsync(); oldCompletion.SetResult(Reading(ownerA, 1000000)); await old;
            Require(profile.Snapshot?.Lifetime == 2000000 && profile.Snapshot.AccountKey == current.AccountKey, "Late account A response overwrote account B.");
            response = _ => Task.FromException<ProfileUsageSnapshot>(new IOException("Synthetic transport failure"));
            await profile.RefreshAsync(true);
            Require(profile.Status == ProfileUsageStatus.Unavailable && profile.Snapshot?.Lifetime == 2000000, "Transient failure discarded same-account history.");
            var lateFailure = Pause(); var lateFailureTask = profile.RefreshAsync(true); tasks.Add(lateFailureTask); await Idle();
            current = Credential("c"); lateFailure.SetException(new IOException("Synthetic late transport failure")); await lateFailureTask;
            Require(profile.Snapshot is null && profile.Status == ProfileUsageStatus.CredentialsUnavailable, "Failed old-account request retained history until the next poll.");
            response = _ => Task.FromResult(Reading(current, 2000000)); await profile.RefreshAsync();
            Button("usage.history.week").Focus();
            Require(pane.IsKeyboardFocusWithin, "Account privacy fixture did not acquire keyboard focus.");
            unavailable = true; await profile.RefreshAsync(); await Idle();
            Require(profile.Status == ProfileUsageStatus.CredentialsUnavailable && profile.Snapshot is null, "Missing credentials retained another account's history.");
            Require(!Descendants<AnimatedMetric>(pane).Any(x => x.FontSize == 20), "Keyboard focus kept account totals visible after identity was lost.");
            Require(Descendants<TextBlock>(pane).Any(x => x.Text == "Codex sign-in is unavailable"), "Account error status is missing from Usage.");
            unavailable = false; response = _ => Task.FromResult(Reading(current, 2000000)); await profile.RefreshAsync();
            busy = true; await profile.RefreshAsync(); Require(profile.Snapshot is null && profile.Status == ProfileUsageStatus.Idle, "Account operation retained history.");
            busy = false; await profile.RefreshAsync();
            var beforeWeek = calls;
            settings.Save(settings.Current with { WeekStart = settings.Current.WeekStart == WeekStart.Monday ? WeekStart.Sunday : WeekStart.Monday });
            await profile.RefreshAsync(); Require(calls == beforeWeek + 1 && profile.Snapshot is not null, "Week-start change did not refetch account totals.");
            var staleDay = Pause(); var dayTask = profile.RefreshAsync(true); tasks.Add(dayTask); await Idle(); now = now.AddDays(1);
            staleDay.SetResult(Reading(current, 7777777)); await dayTask;
            Require(profile.Snapshot is null && profile.Status == ProfileUsageStatus.Idle, "Response from the previous calendar day was accepted.");
            response = _ => Task.FromResult(Reading(current, 2000000)); await profile.RefreshAsync();
            var dayFailure = Pause(); var dayFailureTask = profile.RefreshAsync(true); tasks.Add(dayFailureTask); await Idle(); now = now.AddDays(1);
            dayFailure.SetException(new IOException("Synthetic previous-day failure")); await dayFailureTask;
            Require(profile.Snapshot is null && profile.Status == ProfileUsageStatus.Idle, "Failed previous-day request retained old totals.");
            response = _ => Task.FromResult(Reading(current, 2000000)); await profile.RefreshAsync();
            var busyFailure = Pause(); var busyFailureTask = profile.RefreshAsync(true); tasks.Add(busyFailureTask); await Idle(); busy = true;
            busyFailure.SetException(new IOException("Synthetic switching failure")); await busyFailureTask;
            Require(profile.Snapshot is null && profile.Status == ProfileUsageStatus.Idle, "Failed request during account switch retained old totals.");
            busy = false; response = _ => Task.FromResult(Reading(current, 2000000)); await profile.RefreshAsync();
            var disabledCompletion = Pause(); var disabledTask = profile.RefreshAsync(true); tasks.Add(disabledTask); await Idle();
            settings.Save(settings.Current with { ProfileSyncEnabled = false });
            disabledCompletion.SetResult(Reading(current, 8888888)); await disabledTask;
            Require(profile.Snapshot is null && profile.Status == ProfileUsageStatus.Disabled, "Disabling history accepted an in-flight response.");
            File.WriteAllText(Path.Combine(directory, "windows-profile-history.json"), JsonSerializer.Serialize(new { completed = true, calls,
                checks = new List<string> { "Explicit enable persists; previews remain offline", "Today stays local; server history is never double-counted", "Account and local period details are distinct",
                    "Same-workspace account switch rejects late response", "Transient error retains only same-account snapshot", "Missing credentials and account operations clear history", "Week/day changes invalidate old totals", "Disable rejects in-flight response" } }));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            foreach (var completion in unfinished) completion.TrySetCanceled();
            Restore(profile.Dispose); Restore(() => window?.Close()); Restore(() => localStore?.Dispose());
            try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            Restore(() => settings.Save(before));
        }
        if (cleanup.Count > 0) throw new AggregateException("Profile history fixture cleanup failed", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
