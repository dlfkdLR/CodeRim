using System.Globalization;
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
    private static async Task AnalyticsStateRegression(DashboardStore store, AppSettingsStore settings, string directory)
    {
        var previousUsage = store.Usage.GetValueOrDefault("codex"); var previousEvents = store.Events.GetValueOrDefault("codex");
        var previousCost = settings.Current.CostEstimatesEnabled; var previousProvider = settings.Current.UsageProvider;
        var previousMetadata = store.SessionDetails.GetValueOrDefault("codex");
        var previousAgents = settings.Current.AgentDetailsEnabled; var previousAttachments = settings.Current.AttachmentMetadataEnabled;
        var wasRefreshing = store.RefreshingProviders.Contains("codex");
        Window? window = null; Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            settings.Save(settings.Current with { CostEstimatesEnabled = true, UsageProvider = "codex", AgentDetailsEnabled = true, AttachmentMetadataEnabled = true });
            store.Usage.Remove("codex"); store.Events.Remove("codex");
            var pane = new UsagePane(store, settings, "codex", _ => { }) { Width = 500 };
            window = new Window { Content = pane, Width = 550, Height = 740, Title = "Analytics state fixture" }; window.Show(); await Idle();
            Descendants<Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.destination.activity").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            bool Has(string id) => Descendants<FrameworkElement>(pane).Any(x => AutomationProperties.GetAutomationId(x) == id);
            Require(Has("usage.analytics.loading") && !Has("analytics.summary.tokens"), "Pending analytics fabricated a numeric total");
            var indicator = Descendants<LimitActivityIndicator>(pane).Single();
            Require(indicator.IsRunning == Motion.Enabled, "Analytics loading indicator ignores the motion policy");
            Capture(pane, System.IO.Path.Combine(directory, "windows-analytics-loading.png"));

            store.Usage["codex"] = UsageSnapshot.Empty; store.Events["codex"] = []; pane.Update(); await Idle();
            Require(!indicator.IsRunning, "Removed analytics loading indicator retained its animation timer");
            void EmptyRange()
            {
                var total = Descendants<StackPanel>(pane).First(x => AutomationProperties.GetAutomationId(x) == "analytics.summary.tokens");
                Require(Descendants<TextBlock>(total).Any(x => x.Text == "0") && Descendants<TextBlock>(pane).Any(x => x.Text == "Estimate unavailable")
                    && Descendants<TextBlock>(pane).Any(x => x.Text == "No model-tagged usage in this range.")
                    && !Descendants<Button>(pane).Any(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.cost.", StringComparison.Ordinal)),
                    "An empty analytics range lost its zero total/model state or invented a measured cost");
            }
            EmptyRange();
            Descendants<Button>(pane).Last(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.tokens.", StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            var details = Descendants<StackPanel>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.bucket-details");
            Require(Descendants<TextBlock>(details).Any(x => x.Text == "Estimate unavailable"), "An empty range's selected interval invented a zero-dollar estimate");
            Capture(pane, System.IO.Path.Combine(directory, "windows-analytics-empty.png"));

            var past = DateTimeOffset.Now.AddDays(-10);
            store.Events["codex"] = [new("old-range", past, new(123, 0, 0, 0), "gpt-5.6-sol")];
            store.Usage["codex"] = new(TokenUsage.Zero, TokenUsage.Zero, new(123, 0, 0, 0), new(123, 0, 0, 0), DataQuality.Exact, past);
            pane.Update(); await Idle(); EmptyRange();
            Descendants<RadioButton>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.range.30d").IsChecked = true; await Idle();
            Require(Descendants<TextBlock>(pane).Any(x => x.FontSize == 28 && x.Text == 123L.ToString("N0", CultureInfo.CurrentCulture)), "Changing range did not recover older records");
            store.Usage["codex"] = store.Usage["codex"] with { Quality = DataQuality.Partial }; pane.RefreshReadings(); await Idle();
            var focused = Descendants<Button>(pane).Last(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.tokens.", StringComparison.Ordinal));
            Require(focused.Focus() && pane.IsKeyboardFocusWithin, "Analytics fixture did not focus a chart before its refresh failure");
            store.Usage["codex"] = LocalTokenPresentation.AfterFailure(store.Usage["codex"]); pane.RefreshReadings(); await Idle();
            Require(Has("usage.analytics.stale") && Descendants<TextBlock>(pane).Any(x => x.FontSize == 28 && x.Text == "123")
                && Descendants<TextBlock>(pane).Any(x => x.Text == "Partial local history"), "Focused failed refresh concealed retained partial history or its warning");
            Capture(pane, System.IO.Path.Combine(directory, "windows-analytics-stale.png"));

            pane.Back(); await Idle();
            Require(Descendants<TextBlock>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.today.cost").Text.Contains("partial history", StringComparison.Ordinal),
                "Overview cost lost the retained partial-history disclosure");
            Descendants<Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.destination.activity").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Descendants<RadioButton>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.range.30d").IsChecked = true; await Idle();

            Descendants<Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.model.gpt-5.6-sol").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Require(Has("usage.analytics.stale") && Descendants<TextBlock>(pane).Any(x => x.Text == "Partial local history"), "Model detail lost retained partial history");

            store.Usage["codex"] = UsageSnapshot.Empty with { Quality = DataQuality.Error }; store.RefreshingProviders.Add("codex"); pane.RefreshReadings(); await Idle();
            Require(Has("usage.analytics.unavailable") && !Has("analytics.summary.tokens") && !Has("usage.analytics.loading"),
                "Model detail allowed quota refresh to mask a local-history failure or displayed retained events as current");
            pane.Back(); await Idle();
            Require(Has("usage.analytics.unavailable") && !Has("analytics.summary.tokens"), "Activity allowed retained events through the first-error guard");
            Capture(pane, System.IO.Path.Combine(directory, "windows-analytics-unavailable.png"));
            store.Usage["codex"] = new(TokenUsage.Zero, TokenUsage.Zero, new(123, 0, 0, 0), new(123, 0, 0, 0), DataQuality.Exact, past); pane.Update(); await Idle();
            Require(Has("analytics.summary.tokens") && !Has("usage.analytics.unavailable") && !Has("usage.analytics.stale"), "Analytics did not recover after a successful local read");
            store.Events["codex"] = [new("zero-model", DateTimeOffset.Now, TokenUsage.Zero, "gpt-5.6-sol")]; pane.Update(); await Idle();
            var zeroModel = Descendants<Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.model.gpt-5.6-sol");
            Require(!AutomationProperties.GetName(zeroModel).Contains('$'), "An unavailable zero-valued range priced its model row");
            zeroModel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Require(Descendants<TextBlock>(pane).Any(x => x.Text == "Estimate unavailable"), "An unavailable zero-valued range priced its model detail");
            pane.Back(); await Idle();
            store.Usage["codex"] = UsageSnapshot.Empty; store.Events.Remove("codex"); pane.Update(); await Idle(); EmptyRange();
            Descendants<Button>(pane).First(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.tokens.", StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            var retainedDetails = Descendants<TextBlock>(Descendants<StackPanel>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.bucket-details")).Select(x => x.Text).ToArray();
            pane.Back(); await Idle();
            async Task Open(string id)
            {
                Descendants<Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.destination." + id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            }
            string? ListPeriod() => Descendants<RadioButton>(pane).Single(x => x.GroupName == "UsageRange" && x.IsChecked == true).Tag as string;
            void SetListPeriod(string id) => Descendants<RadioButton>(pane).Single(x => x.GroupName == "UsageRange" && Equals(x.Tag, id)).IsChecked = true;
            await Open("projects"); Require(Equals(ListPeriod(), "30d"), "Projects lost the initial 30D range");
            SetListPeriod("today"); pane.Back(); await Idle(); await Open("activity");
            Require(Descendants<RadioButton>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.range.30d").IsChecked == true,
                "Returning to Usage reset its range or adopted the Projects range");
            Require(Has("usage.bucket-details"), "Returning to Usage discarded its selected interval");
            Require(Descendants<TextBlock>(Descendants<StackPanel>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.bucket-details")).Select(x => x.Text).SequenceEqual(retainedDetails),
                "Returning to Usage restored a different interval date or value");
            Descendants<RadioButton>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.range.7d").IsChecked = true; await Idle();
            Require(!Has("usage.bucket-details"), "Changing the Usage range retained its prior interval selection");
            pane.Back(); await Idle(); await Open("projects"); Require(Equals(ListPeriod(), "today"), "Projects did not preserve its own range");
            pane.Back(); await Idle(); await Open("sessions"); Require(Equals(ListPeriod(), "7d"), "Sessions did not keep its independent 7D default");
            pane.Back(); await Idle(); await Open("activity");
            Descendants<RadioButton>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.range.30d").IsChecked = true; await Idle();
            Descendants<Button>(pane).First(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.tokens.", StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Require(Has("usage.bucket-details"), "Provider-reset fixture did not establish a selected interval");
            Require(store.AvailableUsageProviders.Contains("claude", StringComparer.Ordinal), "Provider-reset fixture requires its synthetic Claude connection");
            pane.SelectProvider("claude"); await Idle();
            Require(Equals(Descendants<ComboBox>(pane).Single(x => AutomationProperties.GetName(x) == "Usage provider").SelectedValue, "claude"), "Provider-reset fixture did not switch to Claude");
            pane.SelectProvider("codex"); await Idle(); await Open("activity");
            Require(Descendants<RadioButton>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.range.7d").IsChecked == true && !Has("usage.bucket-details"),
                "Provider round trip retained another provider's analytics navigation");
            pane.Back(); await Idle(); await Open("projects"); Require(Equals(ListPeriod(), "30d"), "Provider round trip did not reset Projects to 30D");
            SetListPeriod("today");
            settings.Save(settings.Current with { UsageProvider = "claude" }); pane.RefreshReadings(); await Idle();
            settings.Save(settings.Current with { UsageProvider = "codex" }); pane.RefreshReadings(); await Idle(); await Open("projects");
            Require(Equals(ListPeriod(), "30d"), "External provider preference changes retained an old Projects range");
            pane.Back(); await Idle();
            var listTime = DateTimeOffset.Now;
            store.Events["codex"] = [new("list-state", listTime, new(127, 0, 0, 0), "gpt-5.6-sol", "State project", "state-session", "codex", "state-project")];
            var listSnapshot = new UsageSnapshot(new(127, 0, 0, 0), new(127, 0, 0, 0), new(127, 0, 0, 0), new(127, 0, 0, 0), DataQuality.Exact, listTime);
            foreach (var route in new[] { "projects", "sessions" })
            {
                var rowId = route == "projects" ? "usage.project.state-project" : "usage.session.state-session";
                Button? DataRow() => Descendants<Button>(pane).SingleOrDefault(x => AutomationProperties.GetAutomationId(x) == rowId);
                store.Usage.Remove("codex"); await Open(route);
                Require(Has("usage.analytics.loading") && DataRow() is null, route + " exposed cached events before the first local snapshot");
                store.Usage["codex"] = UsageSnapshot.Empty with { Quality = DataQuality.Error }; pane.RefreshReadings(); await Idle();
                Require(Has("usage.analytics.unavailable") && DataRow() is null
                    && Descendants<TextBlock>(pane).Any(x => x.Text == (route == "projects" ? "Projects Unavailable" : "Sessions Unavailable")),
                    route + " failed read fabricated a healthy list");
                store.Usage["codex"] = listSnapshot; pane.RefreshReadings(); await Idle();
                Require(DataRow() is not null && !Has("usage.analytics.unavailable"), route + " did not recover after a successful read");
                DataRow()!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                store.Usage.Remove("codex"); pane.RefreshReadings(); await Idle();
                Require(Has("usage.analytics.loading") && !Descendants<TextBlock>(pane).Any(x => x.Text == "127"), route + " detail exposed values before its local snapshot");
                store.Usage["codex"] = UsageSnapshot.Empty with { Quality = DataQuality.Error }; pane.RefreshReadings(); await Idle();
                Require(Has("usage.analytics.unavailable") && !Descendants<TextBlock>(pane).Any(x => x.Text == "127"), route + " detail exposed cached values after a first-read error");
                store.Usage["codex"] = LocalTokenPresentation.AfterFailure(listSnapshot); pane.RefreshReadings(); await Idle();
                Require(Has("usage.analytics.stale") && Descendants<TextBlock>(pane).Any(x => x.Text == "127"), route + " detail lost its retained snapshot or stale warning");
                pane.Back(); await Idle();
                Require(Has("usage.analytics.stale") && DataRow() is not null, route + " list lost its retained snapshot or stale warning");
                Capture(pane, System.IO.Path.Combine(directory, "windows-analytics-" + route + "-stale.png"));
                pane.Back(); await Idle();
            }
            store.Events["codex"] = [
                new("order-z-old", listTime.AddMinutes(-2), new(19, 0, 0, 0), "gpt-5.6-sol", "Zulu", "old-session", "codex", "z-project"),
                new("order-b", listTime.AddMinutes(-1), new(20, 0, 0, 0), "gpt-5.6-sol", "Alpha", "b-session", "codex", "b-project"),
                new("order-a", listTime.AddMinutes(-1), new(20, 0, 0, 0), "gpt-5.6-sol", "Alpha", "a-session", "codex", "a-project"),
                new("order-new", listTime, new(1, 0, 0, 0), "gpt-5.6-sol", "Zulu", "new-session", "codex", "z-project"),
                new("order-unknown", listTime.AddMinutes(-3), new(1, 0, 0, 0), "gpt-5.6-sol", SessionId: "opaque-123456"),
                new("order-literal", listTime.AddMinutes(-4), new(2, 0, 0, 0), "gpt-5.6-sol", "Unknown project", "literal-session", "codex", "literal-project")];
            store.Usage["codex"] = UsageScanner.Aggregate(store.Events["codex"], listTime, settings.Current.WeekStart, false);
            store.SessionDetails["codex"] = [new("new-session", null, [new("list-image", listTime, 3)]), new("a-session", "new-session", [])];
            Button[] ListRows(string kind) => Descendants<Button>(pane).Where(x => AutomationProperties.GetAutomationId(x).StartsWith("usage." + kind + ".", StringComparison.Ordinal)).ToArray();
            await Open("projects");
            string[] projectOrder = ["a-project", "b-project", "z-project", "literal-project", "unknown"];
            string[] sessionOrder = ["new-session", "a-session", "b-session", "old-session", "opaque-123456", "literal-session"];
            for (var pass = 0; pass < 2; pass++)
            {
                Require(ListRows("project").Select(x => AutomationProperties.GetAutomationId(x)).SequenceEqual(projectOrder.Select(id => "usage.project." + id)),
                    "Project ties did not sort by name and stable identity");
                Require(Descendants<RadioButton>(pane).Count(x => x.GroupName == "UsageRange") == 3
                    && !Descendants<TextBox>(pane).Any() && !Descendants<ComboBox>(pane).Any(x => AutomationProperties.GetName(x) == "Usage period")
                    && !Descendants<TextBlock>(pane).Any(x => x.Text == "Projects" && x.FontSize == 20), "Projects retained extra default list controls or a duplicate title");
                Require(ListRows("project").All(x => x.Padding == new Thickness(16, 10, 16, 10) && x.BorderThickness == new Thickness(0))
                    && AutomationProperties.GetName(ListRows("project")[2]).Contains("2 sessions", StringComparison.Ordinal), "Project rows lost the reference padding or session count");
                pane.Back(); await Idle(); await Open("sessions");
                Require(ListRows("session").Select(x => AutomationProperties.GetAutomationId(x)).SequenceEqual(sessionOrder.Select(id => "usage.session." + id)),
                    "Sessions did not sort by recent activity and stable identity");
                Require(AutomationProperties.GetName(ListRows("session")[0]).StartsWith("Zulu:", StringComparison.Ordinal)
                    && AutomationProperties.GetName(ListRows("session")[4]).StartsWith("Session opaque-1:", StringComparison.Ordinal)
                    && AutomationProperties.GetName(ListRows("session")[5]).StartsWith("Unknown project:", StringComparison.Ordinal),
                    "Session names lost their project, short fallback or legitimate Unknown project folder");
                Require(AutomationProperties.GetName(ListRows("session")[0]).Contains("1 agent", StringComparison.Ordinal)
                    && AutomationProperties.GetName(ListRows("session")[0]).Contains("3 whole-session images", StringComparison.Ordinal), "Session row metadata is absent");
                Require(ListRows("session").All(x => AutomationProperties.GetName(x).Contains('~')), "Known-priced session rows lost their enabled cost estimates");
                if (pass == 0)
                {
                    foreach (var width in new[] { 360d, 450d, 650d })
                    {
                        window.Width = width + 50; pane.Width = width; pane.UpdateLayout(); await Idle();
                        foreach (var row in ListRows("session"))
                            foreach (var text in Descendants<TextBlock>(row))
                            {
                                var bounds = text.TransformToAncestor(row).TransformBounds(new Rect(text.RenderSize));
                                Require(bounds.Left >= -.1 && bounds.Right <= row.ActualWidth + .1, "Session row text exceeds a narrow row");
                            }
                        Capture(pane, System.IO.Path.Combine(directory, "windows-analytics-sessions-width-" + width.ToString(CultureInfo.InvariantCulture) + ".png"));
                    }
                    window.Width = 550; pane.Width = 500;
                    settings.Save(settings.Current with { AgentDetailsEnabled = false, AttachmentMetadataEnabled = false, CostEstimatesEnabled = false }); pane.Update(); await Idle();
                    Require(!AutomationProperties.GetName(ListRows("session")[0]).Contains("agent", StringComparison.Ordinal)
                        && !AutomationProperties.GetName(ListRows("session")[0]).Contains("image", StringComparison.Ordinal)
                        && ListRows("session").All(x => !AutomationProperties.GetName(x).Contains('$')), "Session row visibility preferences were ignored");
                    settings.Save(settings.Current with { AgentDetailsEnabled = true, AttachmentMetadataEnabled = true, CostEstimatesEnabled = true }); pane.Update(); await Idle();
                }
                Capture(pane, System.IO.Path.Combine(directory, "windows-analytics-session-order.png"));
                pane.Back(); await Idle();
                if (pass == 0) { store.Events["codex"] = store.Events["codex"].Reverse().ToArray(); await Open("projects"); }
            }
            store.Events["codex"] = [
                new("mixed-missing", listTime, new(1, 0, 0, 0), "gpt-5.6-sol", SessionId: "mixed-session"),
                new("mixed-old", listTime.AddMinutes(-2), new(2, 0, 0, 0), "gpt-5.6-sol", "Old project", "mixed-session", "codex", "mixed-project"),
                new("mixed-current", listTime.AddMinutes(-1), new(3, 0, 0, 0), "gpt-5.6-sol", "Current project", "mixed-session", "codex", "mixed-project")];
            for (var pass = 0; pass < 2; pass++)
            {
                await Open("projects");
                Require(AutomationProperties.GetName(ListRows("project").Single(x => AutomationProperties.GetAutomationId(x) == "usage.project.mixed-project")).StartsWith("Current project:", StringComparison.Ordinal),
                    "Mixed project metadata changed its name with event order");
                pane.Back(); await Idle(); await Open("sessions");
                Require(AutomationProperties.GetName(ListRows("session").Single()).StartsWith("Current project:", StringComparison.Ordinal),
                    "Mixed session metadata chose an unknown or older project name");
                Require(pane.HandleShortcut(System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.Control), "List find shortcut was not handled"); await Idle();
                var searchBox = Descendants<TextBox>(pane).Single(); searchBox.Text = "mixed-session"; await Idle();
                Require(ListRows("session").Length == 1, "Project display names removed full session-id search");
                searchBox.Text = "no-such-session"; await Idle();
                Require(ListRows("session").Length == 0 && Descendants<TextBlock>(pane).Any(x => x.Text == "No matching sessions."), "List find lost its empty state");
                searchBox.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(searchBox), Environment.TickCount, System.Windows.Input.Key.Escape)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent }); await Idle();
                Require(!Descendants<TextBox>(pane).Any() && ListRows("session").Length == 1
                    && Descendants<RadioButton>(pane).Any(x => x.GroupName == "UsageRange" && x.IsKeyboardFocused), "Closing Find did not clear the query and restore range focus");
                pane.Back(); await Idle();
                store.Events["codex"] = store.Events["codex"].Reverse().ToArray();
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "windows-analytics-states.json"), System.Text.Json.JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Pending without numbers and motion cleanup", "Empty range and selected interval without invented cost", "Older data recovers on range change",
                    "Focused refresh retains partial snapshot with stale warning", "Model and activity errors independent of quota refresh", "Zero-valued model coverage", "Recovery and clear",
                    "Independent retained Usage/Projects/Sessions ranges and reference defaults", "Exact interval restoration and direct/external provider reset",
                    "Projects/Sessions list and detail loading, first error, recovery and retained snapshot warnings",
                    "Stable project ties and recent-session order across reversed input", "Project session names and short fallback without hiding a real Unknown project folder",
                    "Mixed metadata chooses a stable known project and retains full-id search",
                    "Reference list range, plain rows, counts, metadata preferences and narrow bounds", "Optional Find empty state, Escape cleanup and focus" } }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { window?.Close(); } catch (Exception error) { cleanup.Add(error); }
            try { if (previousUsage is null) store.Usage.Remove("codex"); else store.Usage["codex"] = previousUsage; } catch (Exception error) { cleanup.Add(error); }
            try { if (previousEvents is null) store.Events.Remove("codex"); else store.Events["codex"] = previousEvents; } catch (Exception error) { cleanup.Add(error); }
            try { if (previousMetadata is null) store.SessionDetails.Remove("codex"); else store.SessionDetails["codex"] = previousMetadata; } catch (Exception error) { cleanup.Add(error); }
            try { if (!wasRefreshing) store.RefreshingProviders.Remove("codex"); } catch (Exception error) { cleanup.Add(error); }
            try { settings.Save(settings.Current with { CostEstimatesEnabled = previousCost, UsageProvider = previousProvider, AgentDetailsEnabled = previousAgents, AttachmentMetadataEnabled = previousAttachments }); } catch (Exception error) { cleanup.Add(error); }
        }
        if (failure is not null && cleanup.Count > 0) throw new AggregateException("Analytics state fixture and cleanup failed", cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanup.Count > 0) throw new AggregateException("Analytics state fixture cleanup failed", cleanup);
    }
}
