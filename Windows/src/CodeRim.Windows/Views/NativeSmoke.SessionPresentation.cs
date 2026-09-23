using System.Collections;
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
    private static async Task SessionPresentationRegression(DashboardWindow dashboard, DashboardStore store, AppSettingsStore settings, string directory)
    {
        var before = settings.Current; var sessionsBefore = store.Sessions;
        var eventsBefore = store.Events["codex"]; var detailsBefore = store.SessionDetails["codex"];
        using var releaseHistory = new ManualResetEventSlim();
        Task oldRead = Task.CompletedTask;
        try
        {
            settings.Save(before with { Visibility = NotchVisibility.AlwaysShow, CompletionSound = false, ShowUnknownSessions = false, ShowSessionDuration = false, ShowSessionTokens = false });
            dashboard.Navigate("notch"); await Idle();
            foreach (var label in new[] { "Show tasks with unknown status", "Show task duration", "Show tokens per chat" })
            {
                var toggle = Descendants<CheckBox>(dashboard).Single(x => AutomationProperties.GetName(x) == label);
                toggle.IsChecked = true; await Idle();
            }
            var persisted = new AppSettingsStore().Current;
            Require(persisted.ShowUnknownSessions && persisted.ShowSessionDuration && persisted.ShowSessionTokens, "Task Activity controls failed to persist.");
            var now = DateTimeOffset.UtcNow;
            store.UpdateSessionActivity([
                new("root", "codex", "Local task", "busy", now.AddMinutes(-3)) { UsageSessionId = "root" },
                new("remote", "codex", "Remote task", "unavailable", now.AddMinutes(-3)) { RemoteHostId = "remote", CodexThreadId = "11111111-2222-3333-4444-555555555555" },
                new("empty", "codex", "No usage record", "idle", now) { UsageSessionId = "empty" }]);
            store.SessionDetails["codex"] = [new("child", "root", [])];
            var original = new UsageEvent("old", now, new(100, 0, 20), SessionId: "root");
            var blocked = new PausedHistory(original, Environment.CurrentManagedThreadId, releaseHistory);
            store.Events["codex"] = blocked;
            Require(store.TokensForSessions("codex").Count == 0, "Pending task tokens fabricated a value.");
            oldRead = store.WaitForSessionTokensAsync("codex");
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(blocked.WorkerThread != Environment.CurrentManagedThreadId, "Token history was enumerated on the dispatcher.");
            // Replace the source while its first read is paused. Only the new
            // generation may reach the screen after both workers finish.
            store.Events["codex"] = [new("new", now, new(200, 0, 30), SessionId: "root"), new("child", now, new(40, 0, 5), SessionId: "child")];
            var latest = store.WaitForSessionTokensAsync("codex"); releaseHistory.Set();
            await Task.WhenAll(oldRead, latest);
            Require(store.TokensForSessions("codex").Count == 1 && store.TokensForSessions("codex")["root"] == 275, "A stale calculation replaced current descendant totals.");
            var popover = NotchPopover.Create("codex", store, settings.Current, _ => { });
            popover.Measure(new Size(400, 1000)); popover.Arrange(new Rect(0, 0, popover.DesiredSize.Width, popover.DesiredSize.Height)); popover.UpdateLayout();
            var labels = Descendants<TextBlock>(popover).Select(x => x.Text).ToArray();
            Require(labels.Contains("3 min · 275 tokens", StringComparer.Ordinal) && labels.Contains("unknown", StringComparer.Ordinal), "Optional task duration, descendant tokens or unknown state is missing.");
            Require(!labels.Contains("0 tokens", StringComparer.Ordinal), "An unmeasured task displayed fabricated zero tokens.");
            Capture(popover, Path.Combine(directory, "windows-task-activity.png"));
            releaseHistory.Reset();
            var disabledRead = new PausedHistory(original, Environment.CurrentManagedThreadId, releaseHistory);
            store.Events["codex"] = disabledRead;
            oldRead = store.WaitForSessionTokensAsync("codex");
            await disabledRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            settings.Save(settings.Current with { ShowUnknownSessions = false, ShowSessionDuration = false, ShowSessionTokens = false });
            releaseHistory.Set(); await oldRead;
            Require(!disabledRead.EnumerationCompleted, "Turning task tokens off failed to cancel an in-progress history enumeration.");
            popover = NotchPopover.Create("codex", store, settings.Current, _ => { });
            Require(!Descendants<TextBlock>(popover).Any(x => x.Text.Contains("275 tokens", StringComparison.Ordinal) || x.Text == "unknown" || x.Text.Contains("3 min", StringComparison.Ordinal)), "Disabled Task Activity options retained their content.");
            File.WriteAllText(Path.Combine(directory, "windows-task-activity.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Native toggles persist", "Background history enumeration leaves dispatcher available", "Cancelled stale source cannot replace latest descendant total", "Unknown tasks are explicit and optional", "Unmeasured tasks stay blank", "Disabled duration and tokens disappear" } }));
        }
        finally
        {
            releaseHistory.Set();
            try { await oldRead; }
            finally
            {
                store.Events["codex"] = eventsBefore; store.SessionDetails["codex"] = detailsBefore; store.UpdateSessionActivity(sessionsBefore);
                settings.Save(before); dashboard.Navigate("usage");
            }
        }
    }

    private sealed class PausedHistory(UsageEvent item, int dispatcherThread, ManualResetEventSlim release) : IReadOnlyList<UsageEvent>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WorkerThread { get; private set; }
        public bool EnumerationCompleted { get; private set; }
        public int Count => 1;
        public UsageEvent this[int index] => index == 0 ? item : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<UsageEvent> GetEnumerator()
        {
            WorkerThread = Environment.CurrentManagedThreadId;
            Require(WorkerThread != dispatcherThread, "Token enumeration blocked the UI thread.");
            Started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Native token regression did not release history.");
            yield return item;
            EnumerationCompleted = true;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
