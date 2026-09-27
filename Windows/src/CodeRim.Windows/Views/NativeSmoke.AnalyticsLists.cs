using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private sealed class AnalyticsMetadataProbe(IReadOnlyList<SessionDetails> items) : IReadOnlyList<SessionDetails>
    {
        internal int Enumerations { get; private set; }
        public int Count => items.Count;
        public SessionDetails this[int index] => items[index];
        public IEnumerator<SessionDetails> GetEnumerator() { Enumerations++; return items.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static async Task AnalyticsListsRegression(DashboardStore store, AppSettingsStore settings, string directory)
    {
        var previousUsage = store.Usage.GetValueOrDefault("codex"); var previousEvents = store.Events.GetValueOrDefault("codex");
        var previousMetadata = store.SessionDetails.GetValueOrDefault("codex"); var previousLabels = store.CodexAnalyticsLabels;
        var previousSettings = settings.Current; Window? window = null; Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            Require(store.Synthetic, "Analytics list fixture needs the isolated preview store");
            settings.Save(previousSettings with { UsageProvider = "codex", CostEstimatesEnabled = false,
                AgentDetailsEnabled = true, AttachmentMetadataEnabled = true });
            var now = DateTimeOffset.Now;
            store.Events["codex"] = Enumerable.Range(0, 121).Select(index => new UsageEvent("list-" + index,
                now.AddSeconds(-index), new(index + 1, 0, 0), "gpt-5.6-sol", "Task " + index.ToString("D3", CultureInfo.InvariantCulture),
                "session-" + index, "codex", "project-" + index))
                .Append(new("list-old", now.AddDays(-10), new(500, 0, 0), "gpt-5.6-sol", "Older task", "session-old", "codex", "project-old")).ToArray();
            var metadata = new AnalyticsMetadataProbe(Enumerable.Range(0, 121).Select(index => new SessionDetails("session-" + index,
                index % 2 == 1 ? "session-0" : null, [new("image-" + index, now.AddDays(-20), 2)]))
                .Append(new("session-old", "session-0", [])).ToArray());
            store.SessionDetails["codex"] = metadata; store.CodexAnalyticsLabels = new Dictionary<string, CodexAnalyticsLabel>();
            store.Usage["codex"] = UsageScanner.Aggregate(store.Events["codex"], now, settings.Current.WeekStart, false);
            var pane = new UsagePane(store, settings, "codex", _ => { }) { Width = 360 };
            // Settings owns one outer viewport; do not introduce a popover-sized inner scrollbar.
            var viewport = new ScrollViewer { Content = pane, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            window = new Window { Content = viewport, Width = 410, Height = 560, Title = "Continuous analytics lists" };
            window.Show(); await Idle();
            Button FindButton(string id) => Descendants<Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == id);
            Button[] Rows(string kind) => Descendants<Button>(pane).Where(x => AutomationProperties.GetAutomationId(x).StartsWith("usage." + kind + ".", StringComparison.Ordinal)).ToArray();
            async Task Click(string id) { FindButton(id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(); }
            async Task Range(string id)
            {
                Descendants<RadioButton>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.range." + id).IsChecked = true;
                await Idle();
            }
            void Continuous(string kind, int count)
            {
                Require(Rows(kind).Length == count, "Analytics list hides rows behind a page boundary: " + kind);
                Require(!Descendants<Button>(pane).Any(x => x.Content is string text && text.StartsWith("Show more", StringComparison.Ordinal)), "Analytics retained a manual pagination action");
                foreach (var row in Rows(kind))
                {
                    var scrollCount = 0;
                    for (DependencyObject? parent = row; parent is not null; parent = VisualTreeHelper.GetParent(parent))
                        if (parent is ScrollViewer scroll)
                        {
                            scrollCount++;
                            Require(ReferenceEquals(scroll, viewport), "Analytics added a nested list viewport");
                        }
                    Require(scrollCount == 1, "Analytics row is outside its single outer viewport");
                }
                Require(viewport.ScrollableWidth < 1, "Analytics added horizontal scrolling");
            }
            async Task End(string id, string capture)
            {
                viewport.ScrollToEnd(); await Idle();
                var row = FindButton(id); var bounds = row.TransformToAncestor(viewport).TransformBounds(new Rect(row.RenderSize));
                Require(bounds.Top >= -.1 && bounds.Bottom <= viewport.ActualHeight + .1, "The final analytics row is clipped at the scroll boundary");
                foreach (var text in Descendants<TextBlock>(row))
                {
                    var textBounds = text.TransformToAncestor(row).TransformBounds(new Rect(text.RenderSize));
                    Require(textBounds.Left >= -.1 && textBounds.Right <= row.ActualWidth + .1, "A long-list row overflows its narrow width");
                }
                Capture(viewport, Path.Combine(directory, capture));
                Require(row.Focus() && row.IsKeyboardFocused, "The last analytics row cannot receive keyboard focus");
            }
            void Detail(string title, string tokens)
            {
                var header = Descendants<FrameworkElement>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.entity.header");
                Require(Descendants<TextBlock>(header).Select(x => x.Text).SequenceEqual(new[] { title, tokens, "tokens" }), "Late-list navigation opened a different entity or total");
            }
            await Click("usage.destination.projects"); Continuous("project", 122);
            await End("usage.project.project-0", "windows-analytics-project-list-end.png");
            await Click("usage.project.project-0"); Detail("Task 000", "1"); pane.Back(); await Idle(); Continuous("project", 122);
            await Range("7d"); Continuous("project", 121); pane.Back(); await Idle();
            await Click("usage.destination.sessions"); Continuous("session", 121);
            void Metadata(int agents)
            {
                var name = AutomationProperties.GetName(FindButton("usage.session.session-0"));
                Require(name.Contains(agents.ToString(CultureInfo.CurrentCulture) + " agents", StringComparison.Ordinal)
                    && name.Contains("2 whole-session images", StringComparison.Ordinal), "Full-list metadata lost ranged children or whole-session images");
            }
            Metadata(60);
            await End("usage.session.session-120", "windows-analytics-session-list-end.png");
            await Click("usage.session.session-120"); Detail("Task 120", "121"); pane.Back(); await Idle(); Continuous("session", 121);
            var enumerations = metadata.Enumerations; pane.Update(); await Idle(); Continuous("session", 121);
            Require(metadata.Enumerations == enumerations + 1, "Each list row rescans the full metadata source");
            await Range("30d"); Continuous("session", 122); Metadata(61); await Range("7d"); Continuous("session", 121); Metadata(60);
            Require(pane.HandleShortcut(Key.F, ModifierKeys.Control), "Analytics Find shortcut was lost"); await Idle();
            var filter = Descendants<TextBox>(pane).Single(); filter.Text = "Task 120"; await Idle(); Continuous("session", 1);
            Require(AutomationProperties.GetAutomationId(Rows("session").Single()) == "usage.session.session-120", "Find excluded an entity beyond the old page boundary");
            filter.Text = ""; await Idle(); Continuous("session", 121);
            window.Width = 700; pane.Width = 650; await Idle(); Continuous("session", 121);
            await End("usage.session.session-120", "windows-analytics-session-list-wide.png");
            File.WriteAllText(Path.Combine(directory, "windows-analytics-lists.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "All 122 projects and 121 recent sessions are present without manual pagination", "Final rows visible and keyboard focusable in one outer viewport",
                    "Late-list project/session navigation retains own totals and Back restores the full list", "Refresh, saved range and 7D/30D filtering retain all matching entities",
                    "Find locates entities beyond the former 40-row page", "360/650-width final-row bounds and captures",
                    "One metadata-source scan per render, range-scoped children and whole-session image counts" } }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => window?.Close()); Restore(() => store.CodexAnalyticsLabels = previousLabels);
            Restore(() => { if (previousUsage is null) store.Usage.Remove("codex"); else store.Usage["codex"] = previousUsage; });
            Restore(() => { if (previousEvents is null) store.Events.Remove("codex"); else store.Events["codex"] = previousEvents; });
            Restore(() => { if (previousMetadata is null) store.SessionDetails.Remove("codex"); else store.SessionDetails["codex"] = previousMetadata; });
            Restore(() => settings.Save(previousSettings));
        }
        if (cleanup.Count > 0) throw new AggregateException("Analytics list fixture cleanup failed", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
