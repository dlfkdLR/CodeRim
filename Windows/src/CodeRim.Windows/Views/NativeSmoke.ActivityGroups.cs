using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task ActivityGroupsRegression(NotchWindow notch, DashboardStore store, AppSettingsStore settings, string directory)
    {
        var before = settings.Current; var previous = store.Sessions; var events = store.Events["codex"]; var details = store.SessionDetails["codex"];
        var previousCursor = store.Readings.GetValueOrDefault("cursor");
        Window? fixture = null; Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            store.Readings["cursor"] = new("cursor", ReadingState.Ready, [new("quota", "Quota", 25)], DateTimeOffset.Now);
            settings.Save(before with { Visibility = NotchVisibility.AlwaysShow, CompletionSound = false, PeekOnCompletion = false,
                ShowSessionTokens = true, ShowSessionDuration = true, EnabledProviders = before.EnabledProviders.Concat(["codex", "cursor"]).Distinct(StringComparer.Ordinal).ToArray() });
            const string parent = "11111111-1111-4111-8111-111111111111";
            const string child = "22222222-2222-4222-8222-222222222222";
            static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
            var now = DateTimeOffset.Now;
            store.SessionDetails["codex"] = [new(Hash(child), Hash(parent), [])];
            store.Events["codex"] = [new("parent", now, new(100, 0, 20), SessionId: Hash(parent)),
                new("child", now, new(30, 0, 4), SessionId: Hash(child))];
            store.UpdateSessionActivity([new("agent", "codex", "Project", "waiting", now.AddMinutes(-2)) {
                CodexThreadId = child, Detail = "Review changes", ParentThreadId = parent, ParentThreadTitle = "Main task",
                UsageSessionId = Hash(child) }, new("other", "codex", "Other project", "busy", now)]);
            await store.WaitForSessionTokensAsync("codex");
            var expansion = new SessionExpansionState(); var navigations = 0;
            FrameworkElement Card() => NotchPopover.Create("codex", store, settings.Current, _ => navigations++, availableHeight: 260, expansion);
            fixture = new Window { Width = 320, Height = 400, ShowInTaskbar = false, Content = Card() }; fixture.Show(); await Idle();
            Button Disclosure(string id) => Descendants<Button>(fixture).Single(x => AutomationProperties.GetAutomationId(x) == id);
            Disclosure("notch.sessions.showAll").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Require(expansion.Expanded && navigations == 0 && fixture.IsVisible, "Task disclosure navigated away instead of expanding inline.");
            var rows = Descendants<Button>(fixture).Where(x => AutomationProperties.GetAutomationId(x).StartsWith("notch.session.", StringComparison.Ordinal)).ToArray();
            Require(rows.Length == 3, "Expanded task groups lost a parent, child or independent task.");
            var context = rows.Single(x => AutomationProperties.GetItemStatus(x) == "Parent chat");
            Require(!Descendants<SessionStatusRing>(context).Any(), "Context-only parent invented a live-state ring.");
            Require(Descendants<TextBlock>(context).Any(x => x.Text == "Main task")
                && Descendants<TextBlock>(context).Any(x => x.Text == "154 tokens"), "Parent chat is missing its title or combined tokens.");
            var childRow = rows.Single(x => AutomationProperties.GetAutomationId(x) == "notch.session.agent");
            Require(Descendants<TextBlock>(childRow).Any(x => x.Text == "↳ Review changes")
                && !Descendants<TextBlock>(childRow).Any(x => x.Text.Contains("tokens", StringComparison.Ordinal)), "Child title or token deduplication differs from the reference.");
            Require(AutomationProperties.GetName(childRow).Contains("of Main task", StringComparison.Ordinal), "Sub-agent accessibility lost parent context.");
            Capture(fixture, Path.Combine(directory, "windows-task-groups-expanded.png"));
            fixture.Content = Card(); await Idle();
            Require(Descendants<Button>(fixture).Any(x => AutomationProperties.GetAutomationId(x) == "notch.sessions.showLess"), "A popup refresh discarded expansion state.");
            Disclosure("notch.sessions.showLess").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Require(!expansion.Expanded && navigations == 0, "Collapse navigated away or failed to reset state.");
            Capture(fixture, Path.Combine(directory, "windows-task-groups-collapsed.png"));
            fixture.Close(); fixture = null;
            var crowded = store.Sessions.Concat(Enumerable.Range(0, 40).Select(index => new SessionActivity("extra-" + index,
                "codex", "Additional task " + index, "idle", now))).ToArray();
            store.UpdateSessionActivity(crowded); notch.OpenProvider("codex"); await Idle();
            var showAll = Descendants<Button>(notch.PopupContent!).Single(x => AutomationProperties.GetAutomationId(x) == "notch.sessions.showAll");
            showAll.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Require(notch.PopupContent!.IsKeyboardFocusWithin, "Native task disclosure did not retain keyboard focus.");
            var changed = crowded.Select(x => x.Id == "agent" ? x with { Detail = "Changed after expansion", State = "busy" } : x).ToArray();
            var notifications = 0;
            System.ComponentModel.PropertyChangedEventHandler countChanges = (_, _) => notifications++;
            store.PropertyChanged += countChanges;
            try
            {
                store.UpdateSessionActivity(changed);
                Require(notifications == 1, "Task mutation did not publish exactly one state change.");
                store.UpdateSessionActivity(changed);
                Require(notifications == 1, "An unchanged task snapshot published a duplicate change.");
                await MotionUntil(() => notch.PopupContent is { } content && content.IsKeyboardFocusWithin
                    && Descendants<TextBlock>(content).Any(x => x.Text == "↳ Changed after expansion"),
                    "Focused expanded popup stopped applying live task updates.");
            }
            finally
            {
                store.PropertyChanged -= countChanges;
                try
                {
                    File.WriteAllText(Path.Combine(directory, "windows-task-groups-live-state.json"), JsonSerializer.Serialize(new {
                        notifications, popupOpen = notch.PopupIsOpen, focused = notch.PopupContent?.IsKeyboardFocusWithin,
                        focusId = Keyboard.FocusedElement is DependencyObject currentFocus ? AutomationProperties.GetAutomationId(currentFocus) : null,
                        updatedTitle = notch.PopupContent is { } content && Descendants<TextBlock>(content).Any(x => x.Text == "↳ Changed after expansion") }));
                }
                catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            }
            Require(Keyboard.FocusedElement is DependencyObject focus && AutomationProperties.GetAutomationId(focus) == "notch.sessions.showLess",
                "Live task refresh stole disclosure keyboard focus.");
            var preservedPopup = notch.PopupContent!; var preservedFocus = Keyboard.FocusedElement;
            var taskScroll = Descendants<ScrollViewer>(preservedPopup).Single();
            Require(taskScroll.ScrollableHeight > 80, "Expanded task fixture is not scrollable.");
            taskScroll.ScrollToVerticalOffset(60); await Idle();
            Require(Math.Abs(taskScroll.VerticalOffset - 60) < .1, "Expanded task fixture did not reach a nonzero scroll position.");
            Require(Descendants<ProviderRing>(notch).Any(x => x.ProviderId == "cursor"), "Unrelated provider was absent before the membership change.");
            store.Readings["cursor"] = new("cursor", ReadingState.Error, []); notch.RefreshReadings(); await Idle();
            Require(!Descendants<ProviderRing>(notch).Any(x => x.ProviderId == "cursor")
                && notch.PopupIsOpen && ReferenceEquals(preservedPopup, notch.PopupContent)
                && ReferenceEquals(preservedFocus, Keyboard.FocusedElement)
                && Descendants<Button>(notch.PopupContent!).Any(x => AutomationProperties.GetAutomationId(x) == "notch.sessions.showLess")
                && Math.Abs(taskScroll.VerticalOffset - 60) < .1,
                "Unrelated provider failure reset the expanded task popup, disclosure focus or nonzero scroll position.");
            Capture(notch.PopupContent!, Path.Combine(directory, "windows-task-groups-live.png"));
            File.WriteAllText(Path.Combine(directory, "windows-task-groups.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Parent and children stay together", "Context parent has no live ring or duration", "Only parent displays combined tokens",
                    "Inline expansion and collapse preserve the popup", "Expansion survives content refresh", "Sub-agent accessibility includes parent title",
                    "Production popup applies live task changes while preserving disclosure keyboard focus",
                    "Unrelated provider failure preserves the expanded task popup, disclosure focus and nonzero scroll position" } }));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => fixture?.Close());
            Restore(() => notch.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(notch), Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent }));
            Restore(() => { store.Events["codex"] = events; store.SessionDetails["codex"] = details; store.UpdateSessionActivity(previous); });
            Restore(() => { if (previousCursor is null) store.Readings.Remove("cursor"); else store.Readings["cursor"] = previousCursor; });
            Restore(() => settings.Save(before));
        }
        if (cleanup.Count > 0) throw new AggregateException("Task grouping fixture cleanup failed.", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
