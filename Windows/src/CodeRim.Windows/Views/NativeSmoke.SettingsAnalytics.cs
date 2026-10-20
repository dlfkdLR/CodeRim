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
    // Test-only guard for fixtures that replace source dictionaries. A render never
    // manufactures a publication; each successful synthetic read calls the real Store method.
    private sealed class AnalyticsPublicationFixture : IDisposable
    {
        private readonly DashboardStore store;
        private readonly string provider;
        private readonly AnalyticsSourceFrame? source;
        private readonly long? epoch;
        internal AnalyticsPublicationFixture(DashboardStore store, string provider)
        {
            Require(store.Synthetic, "Analytics fixture requires its isolated synthetic store");
            this.store = store; this.provider = provider;
            source = store.AnalyticsSources.GetValueOrDefault(provider);
            epoch = store.LocalAnalyticsEpochs.TryGetValue(provider, out var value) ? value : null;
        }
        public void Dispose()
        {
            if (source is null) store.AnalyticsSources.Remove(provider); else store.AnalyticsSources[provider] = source;
            if (epoch.HasValue) store.LocalAnalyticsEpochs[provider] = epoch.Value; else store.LocalAnalyticsEpochs.Remove(provider);
        }
    }

    private sealed class SettingsAnalyticsClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        internal int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() { Reads++; return Now.ToUniversalTime(); }
    }

    private static async Task SettingsAnalyticsRegression(DashboardStore store, AppSettingsStore settings, string directory)
    {
        Require(store.Synthetic, "Settings analytics tests cannot run against a real source");
        var before = settings.Current;
        var originalUsage = store.Usage.GetValueOrDefault("codex"); var originalEvents = store.Events.GetValueOrDefault("codex");
        var originalOperationMessage = store.DataOperationMessages.GetValueOrDefault("codex");
        var wasRebuilding = store.RebuildingProviders.Contains("codex");
        using var publicationState = new AnalyticsPublicationFixture(store, "codex");
        Window? window = null; Exception? failure = null; var cleanup = new List<Exception>();
        var checks = new List<string>();
        try
        {
            settings.Save(before with { UsageProvider = "codex", AnalyticsEnabled = true, SessionsEnabled = true,
                ShowCachedInput = true, NumberStyle = TokenNumberStyle.Detailed, ReduceMotion = true });
            var through = new DateTimeOffset(new DateTime(2026, 9, 28, 23, 59, 0, DateTimeKind.Local));
            var yesterday = new DateTimeOffset(DateTime.SpecifyKind(through.Date.AddDays(-1).AddHours(12), DateTimeKind.Local));
            UsageEvent Event(string id, DateTimeOffset at, long input, string model, string session, long cached = 0, long output = 0, long writes = 0) =>
                new(id, at, new(input, cached, output, writes), model, "Settings fixture", session, "codex", "fixture-project");
            const string sessionA = "session-a-full-identifier-0123456789";
            UsageEvent[] events = [Event("a", yesterday, 300, "a", sessionA, 100, 100, 50), Event("b", yesterday.AddMinutes(1), 400, "b", "b"),
                Event("c", through.AddMinutes(-5), 100, "a", "c", 40, 0, 30), Event("d", through.AddMinutes(-4), 80, "c", "d"),
                Event("e", through.AddMinutes(-3), 60, "d", "e"), Event("f", through.AddMinutes(-2), 40, "e", "f"),
                Event("g", through.AddMinutes(-1), 20, "f", "g"), Event("older-a", through.AddDays(-10), 600, "a", sessionA)];
            // Deliberately distinguish local calendar totals from the selected range,
            // and source Through from the last event time.
            var local = new UsageSnapshot(new(901, 0, 0), new(902, 0, 0), new(903, 0, 0), new(904, 0, 0), DataQuality.Exact, yesterday);
            store.Events["codex"] = events; store.Usage["codex"] = local;
            store.RecordLocalAnalyticsRead("codex", through);
            var clock = new SettingsAnalyticsClock(through.AddDays(10));
            var pane = new UsagePane(store, settings, "codex", _ => { }, clock);
            var viewport = new ScrollViewer { Content = pane, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            window = new Window { Title = "Settings usage analytics fixture", Content = viewport, Width = 650, Height = 720 };
            window.Show(); await Idle();
            FrameworkElement Find(string id) => Descendants<FrameworkElement>(pane).Single(x => AutomationProperties.GetAutomationId(x) == id);
            bool Has(string id) => Descendants<FrameworkElement>(pane).Any(x => AutomationProperties.GetAutomationId(x) == id);
            Button Button(string id) => (Button)Find(id);
            RadioButton Radio(string id) => (RadioButton)Find(id);
            Expander Disclosure(string id) => (Expander)Find(id);
            string Text(FrameworkElement element) => string.Join(" | ", element is TextBlock own ? new[] { own.Text }
                : Descendants<TextBlock>(element).Select(x => x.Text));
            void Total(long value) => Require(Text(Find("settings.usage.total")).Split(" | ", StringSplitOptions.None)
                .Contains(value.ToString("N0", CultureInfo.CurrentCulture), StringComparer.Ordinal), "Settings total does not match the published source");
            async Task Click(string id) { Button(id).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Idle(); }
            async Task Tab(Key key) { Require(pane.HandleShortcut(key, ModifierKeys.Control), "Usage tab shortcut was not handled"); await Idle(); }
            async Task Range(string id) { Radio("settings.usage.range." + id).IsChecked = true; await Idle(); }
            ComboBox Grouping() => (ComboBox)Find("settings.usage.grouping");
            Button[] Buckets(string prefix = "settings.usage.bucket.") => Descendants<Button>(pane)
                .Where(x => AutomationProperties.GetAutomationId(x).StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            string[] SessionIds() => Descendants<Expander>(Find("settings.usage.topSessions"))
                .Select(x => AutomationProperties.GetAutomationId(x)).ToArray();
            string Legend(string id) => string.Join("|", Descendants<FrameworkElement>(Find(id))
                .Select(x => AutomationProperties.GetName(x)).Where(x => !string.IsNullOrEmpty(x)));
            void Series(string id, long tokens, long total)
            {
                var row = Descendants<FrameworkElement>(Find("settings.usage.legend")).Single(x => AutomationProperties.GetAutomationId(x) == "settings.usage.series." + id);
                var name = AutomationProperties.GetName(row);
                Require(name.Contains(tokens.ToString("N0", CultureInfo.CurrentCulture) + " tokens", StringComparison.Ordinal)
                    && name.Contains(((double)tokens / total).ToString("P1", CultureInfo.CurrentCulture), StringComparison.Ordinal),
                    "Series exact tokens/share disagree: " + id);
            }
            void OuterOnly(FrameworkElement element)
            {
                var count = 0;
                for (DependencyObject? ancestor = element; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                    if (ancestor is ScrollViewer scroll) { count++; Require(ReferenceEquals(scroll, viewport), "Settings analytics added a nested viewport"); }
                Require(count == 1, "Settings section lost its single outer scroll owner");
            }
            Require(Has("settings.usage.analytics") && Radio("settings.usage.tab.analytics").IsChecked == true
                && Radio("settings.usage.range.7d").IsChecked == true, "Settings default is not seven-day analytics");
            Total(1100); Require(Buckets().Length == 7 && Buckets("settings.usage.modelBucket.").Length == 7,
                "Settings charts do not use daily seven-day buckets");
            Require(clock.Reads == 0, "Source-backed analytics read the render clock instead of the successful publication");
            void FrameHeight(Button mark, double height)
            {
                var found = false;
                for (DependencyObject? ancestor = mark; ancestor is not null && !ReferenceEquals(ancestor, pane); ancestor = VisualTreeHelper.GetParent(ancestor))
                    if (ancestor is Grid grid && Math.Abs(grid.Height - height) < .1 && Math.Abs(grid.ActualHeight - height) < .1) found = true;
                Require(found, "Settings chart lost its reference frame height");
            }
            FrameHeight(Buckets()[0], 190); FrameHeight(Buckets("settings.usage.modelBucket.")[0], 160);

            Series("input", 860, 1100); Series("cached", 140, 1100); Series("output", 100, 1100);
            var modelLegend = Legend("settings.usage.modelLegend");
            Require(modelLegend.Contains("500 tokens", StringComparison.Ordinal) && modelLegend.Contains("400 tokens", StringComparison.Ordinal)
                && Descendants<AnimatedMetric>(Find("settings.usage.modelCount")).Single().DisplayedValue == 6, "Model activity omitted the six recorded models");
            var todayId = AutomationProperties.GetAutomationId(Buckets()[^1]);
            await Click(todayId); Total(1100); Series("input", 260, 300); Series("cached", 40, 300); Series("output", 0, 300);
            Require(Has("settings.usage.clearSelection") && Has("settings.usage.selectedDate"), "Daily selection has no caption or clear control");
            Require(Legend("settings.usage.modelLegend") == modelLegend, "Model activity legend adopted the selected day instead of range totals");
            Grouping().SelectedIndex = 1; await Idle();
            Series("model:a", 100, 300); Series("model:b", 0, 300); Series("other-models", 20, 300);
            Require(Legend("settings.usage.modelLegend") == modelLegend, "Total grouping changed model activity's range series");
            await Click("settings.usage.clearSelection"); Total(1100);
            Require(!Has("settings.usage.selectedDate"), "Clear selection retained its selected date");
            Series("model:a", 500, 1100); Series("model:b", 400, 1100); Series("other-models", 20, 1100);
            Grouping().SelectedIndex = 0; await Idle();
            await Click(AutomationProperties.GetAutomationId(Buckets("settings.usage.modelBucket.")[^2]));
            Total(1100); Series("input", 600, 800); Series("cached", 100, 800); Series("output", 100, 800);
            Require(Legend("settings.usage.modelLegend") == modelLegend, "Model-chart selection changed its whole-range legend");
            await Click("settings.usage.clearSelection");
            settings.Save(settings.Current with { ShowCachedInput = false }); pane.Update(); await Idle();
            Series("input", 1000, 1100); Series("output", 100, 1100);
            Require(!Descendants<FrameworkElement>(Find("settings.usage.legend")).Any(x => AutomationProperties.GetAutomationId(x) == "settings.usage.series.cached"), "Hidden cached input remained a separate series");
            settings.Save(settings.Current with { ShowCachedInput = true }); pane.Update(); await Idle();
            checks.Add("Daily token/cache-write conservation, exact shares, selected-day/clear, stable top-five plus Other, independent whole-range model legend");

            var expectedFirst = new[] { "b", sessionA, "c", "d", "e" }.Select(x => "settings.usage.session." + x).ToArray();
            Require(SessionIds().SequenceEqual(expectedFirst), "Top sessions uses recency rather than token rank/ID ties");
            Require(Text(Find("settings.usage.topSessions")).Contains("Share", StringComparison.Ordinal), "Top sessions lost its Share column");
            Disclosure("settings.usage.session." + sessionA).IsExpanded = true; await Idle();
            var disclosed = Text(Disclosure("settings.usage.session." + sessionA));
            Require(disclosed.Contains((400d / 1100).ToString("P1", CultureInfo.CurrentCulture), StringComparison.Ordinal),
                "Top session share is not based on the whole selected range");
            Require(disclosed.Contains(sessionA, StringComparison.Ordinal) && disclosed.Contains("Input", StringComparison.Ordinal) && disclosed.Contains("Output", StringComparison.Ordinal)
                && disclosed.Contains("cache", StringComparison.OrdinalIgnoreCase) && disclosed.Contains("Last active", StringComparison.OrdinalIgnoreCase),
                "Session disclosure lost token decomposition or last activity");
            var disclosureTexts = Descendants<TextBlock>(Disclosure("settings.usage.session." + sessionA)).Select(x => x.Text).ToArray();
            void BreakdownValue(Func<string, bool> matchesLabel, long expected)
            {
                var indices = Enumerable.Range(0, disclosureTexts.Length).Where(index => matchesLabel(disclosureTexts[index])).ToArray();
                Require(indices.Length == 1 && indices[0] + 1 < disclosureTexts.Length
                    && disclosureTexts[indices[0] + 1] == expected.ToString("N0", CultureInfo.CurrentCulture),
                    "Session token breakdown does not bind its label to the exact fixture count");
            }
            BreakdownValue(text => text == "Input", 300);
            BreakdownValue(text => text.Contains("Cached input", StringComparison.OrdinalIgnoreCase), 100);
            BreakdownValue(text => text == "Output", 100);
            var activityText = disclosureTexts.Single(text => text.StartsWith("Last active ", StringComparison.OrdinalIgnoreCase));
            var expectedLastActivity = AnalyticsDateText.Format(yesterday, AnalyticsDateStyle.DayAndTime);
            Require(expectedLastActivity != "Date unavailable" && activityText == "Last active " + expectedLastActivity,
                "Session last activity is not its actual in-range fixture event time");
            await Click("settings.usage.moreSessions"); Require(SessionIds().Length == 7, "Show more did not reveal all ranked sessions");
            await Click("settings.usage.moreSessions"); Require(SessionIds().SequenceEqual(expectedFirst), "Show less did not restore token-ranked top five");
            await Click("settings.usage.moreSessions");
            Disclosure("settings.usage.period.week").IsExpanded = true; await Idle();
            foreach (var (period, tokens) in new[] { ("today", 901L), ("week", 902L), ("month", 903L), ("all-time", 904L) })
            {
                var history = Disclosure("settings.usage.period." + period);
                Require(Text(history).Contains(tokens.ToString("N0", CultureInfo.CurrentCulture), StringComparison.Ordinal),
                    "Local calendar history reused range/account totals: " + period);
                OuterOnly(history);
            }
            await Tab(Key.D3); await Tab(Key.D1); await Tab(Key.D2);
            Require(Disclosure("settings.usage.session." + sessionA).IsExpanded && SessionIds().Length == 7
                && Disclosure("settings.usage.period.week").IsExpanded, "Tabs discarded analytics disclosures");
            await Click("settings.usage.sessionDetails." + sessionA);
            Require(Has("usage.entity.header") && !Has("settings.usage.analytics"), "Session details did not enter the existing detail route");
            Require(Text(Find("usage.entity.header")).Contains("400", StringComparison.Ordinal), "Session detail did not preserve its selected range");
            pane.Back(); await Idle();
            Require(Disclosure("settings.usage.session." + sessionA).IsExpanded && SessionIds().Length == 7, "Back discarded parent session state");
            await Click("settings.usage.periodDetails.week");
            Require(Has("usage.period.total"), "Local calendar disclosure did not open its existing detail route");
            pane.Back(); await Idle();
            Require(Disclosure("settings.usage.period.week").IsExpanded, "Back discarded calendar disclosure state");
            Grouping().SelectedIndex = 1; await Idle(); await Click(todayId); await Range("30d");
            Total(1700); Require(Buckets().Length == 30 && !Has("settings.usage.selectedDate") && SessionIds().Length == 5
                && !Disclosure("settings.usage.session." + sessionA).IsExpanded && Disclosure("settings.usage.period.week").IsExpanded
                && Grouping().SelectedIndex == 1, "Range did not reset only date/session state while preserving grouping/calendar disclosures");
            Disclosure("settings.usage.session." + sessionA).IsExpanded = true; await Idle(); await Click("settings.usage.sessionDetails." + sessionA);
            Require(Has("usage.entity.header") && Text(Find("usage.entity.header")).Contains(1000L.ToString("N0", CultureInfo.CurrentCulture), StringComparison.Ordinal),
                "Thirty-day session detail lost older in-range events"); pane.Back(); await Idle();
            Require(Radio("settings.usage.range.30d").IsChecked == true, "Thirty-day session Back reset range");
            OuterOnly(Find("settings.usage.topSessions")); OuterOnly(Find("settings.usage.modelActivity"));
            viewport.ScrollToEnd(); await Idle(); Require(viewport.ScrollableHeight > 0 && viewport.ScrollableWidth < 1,
                "Long settings content has no single vertical scroll or overflows horizontally");
            Capture(viewport, Path.Combine(directory, "windows-settings-analytics-disclosures.png"));
            checks.Add("Token-ranked top5/ties, exact Input300/cache100/Output100 and actual last activity, in-place session/calendar disclosures, More, detail/Back, range/tab state and one outer viewport");

            settings.Save(settings.Current with { SessionsEnabled = false }); pane.Update(); await Idle();
            Require(!Has("settings.usage.topSessions") && Has("settings.usage.modelActivity"), "Sessions setting hides unrelated analytics or leaves session rows");
            settings.Save(settings.Current with { AnalyticsEnabled = false }); pane.Update(); await Idle();
            Require(!Has("settings.usage.tab.analytics") && Has("usage.overview"), "Disabled analytics did not fall back to Overview");
            settings.Save(settings.Current with { AnalyticsEnabled = true, SessionsEnabled = true }); pane.Update(); await Idle();
            Require(Has("settings.usage.analytics") && Radio("settings.usage.range.30d").IsChecked == true,
                "Re-enabling analytics lost the requested section/range");
            settings.Save(settings.Current with { AnalyticsEnabled = false }); pane.Update(); await Idle(); await Tab(Key.D3);
            settings.Save(settings.Current with { AnalyticsEnabled = true }); pane.Update(); await Idle();
            Require(Radio("settings.usage.tab.limits").IsChecked == true, "Explicit Limits selection was overwritten when analytics re-enabled");
            await Tab(Key.D2);
            pane.SelectProvider("claude"); await Idle(); pane.SelectProvider("codex"); await Idle();
            Require(Radio("settings.usage.tab.analytics").IsChecked == true && Radio("settings.usage.range.7d").IsChecked == true
                && Grouping().SelectedIndex == 0 && !Disclosure("settings.usage.period.week").IsExpanded
                && SessionIds().Length == 5 && !Disclosure("settings.usage.session." + sessionA).IsExpanded, "Provider round trip did not clear its navigation state");
            checks.Add("Analytics disabled/effective Overview then requested-section restoration, explicit Limits override, Sessions setting and provider reset");

            // Keep the selected day through midnight before I/O, then failure. A
            // publication notification on another range must still update it later.
            // New pane has queried only 7D; the previous pane exercised 30D independently.
            pane = new UsagePane(store, settings, "codex", _ => { }, clock); viewport.Content = pane; await Idle();
            store.Usage["codex"] = local with { Quality = DataQuality.Partial, RetainsPartialHistory = true };
            store.RecordLocalAnalyticsRead("codex", through); pane.RefreshReadings(); await Idle();
            Require(Has("settings.usage.partial") && !Has("settings.usage.stale"),
                "Successful Partial source must show its own notice without a failure notice");
            todayId = AutomationProperties.GetAutomationId(Buckets()[^1]); await Click(todayId);
            var retainedIds = Buckets().Select(x => AutomationProperties.GetAutomationId(x)).ToArray();
            var retainedDate = Text(Find("settings.usage.selectedDate")); clock.Now = through.AddMinutes(2);
            pane.RefreshReadings(); await Idle(); Total(1100);
            Require(Buckets().Select(x => AutomationProperties.GetAutomationId(x)).SequenceEqual(retainedIds), "Metadata notification advanced a still-successful frame past midnight");
            store.Usage["codex"] = LocalTokenPresentation.AfterFailure(store.Usage["codex"]); pane.RefreshReadings(); await Idle();
            Total(1100); Require(Has("settings.usage.stale") && !Has("settings.usage.partial")
                && Text(Find("settings.usage.selectedDate")) == retainedDate
                && Buckets().Select(x => AutomationProperties.GetAutomationId(x)).SequenceEqual(retainedIds),
                "Failure changed source through/domain/day or did not prioritize the single last-snapshot notice");
            await Range("30d"); Require(Has("settings.usage.unavailable") && !Has("settings.usage.total"), "A failed unqueried range fabricated retained analytics");
            await Range("7d"); Require(Has("settings.usage.stale") && !Has("settings.usage.partial"), "Returning to queried failed range lost its snapshot or duplicated its status notice");
            var recovery = through.AddMinutes(2);
            store.Events["codex"] = [Event("recovered", recovery.AddSeconds(-1), 77, "recovered", "recovered")];
            store.Usage["codex"] = local with { Quality = DataQuality.Exact };
            store.RecordLocalAnalyticsRead("codex", recovery); pane.RefreshReadings(); await Idle();
            Total(77); Require(!Has("settings.usage.stale") && !Has("settings.usage.partial"), "Successful recovery retained stale flags");
            await Range("30d"); Total(77);
            // Simulate maintenance START, not its old healthy dictionaries. Both
            // prior queries must be revoked before any successful rebuild publication.
            var focusedGrouping = Grouping();
            Require(focusedGrouping.Focus() && pane.IsKeyboardFocusWithin && focusedGrouping.IsKeyboardFocusWithin,
                "Maintenance fixture did not establish keyboard focus in the grouping control");
            Require(store.Usage["codex"].Quality == DataQuality.Exact && store.AnalyticsSources.ContainsKey("codex"),
                "Maintenance fixture must begin with an unchanged healthy local quality and a published source");
            store.LocalAnalyticsEpochs["codex"] = store.LocalAnalyticsEpochs.GetValueOrDefault("codex") + 1;
            store.AnalyticsSources.Remove("codex"); pane.RefreshReadings(); await Idle();
            Require(store.Usage["codex"].Quality == DataQuality.Exact && !Has("settings.usage.total") && !Has("settings.usage.topSessions"),
                "Keyboard focus deferred source/epoch revocation and left old healthy analytics visible");
            store.Usage["codex"] = local with { Quality = DataQuality.Error }; pane.RefreshReadings(); await Idle();
            Require(Has("settings.usage.unavailable") && !Has("settings.usage.total"), "Failed maintenance restored a pre-clear snapshot");
            await Range("7d"); Require(!Has("settings.usage.total"), "Provider epoch did not revoke the other range");
            store.Usage.Remove("codex"); store.Events.Remove("codex"); pane.RefreshReadings(); await Idle();
            Require(Has("settings.usage.loading") && !Has("settings.usage.total"), "Absent source manufactured exact zero");
            store.Usage["codex"] = UsageSnapshot.Empty; store.Events["codex"] = [];
            store.RecordLocalAnalyticsRead("codex", recovery); pane.RefreshReadings(); await Idle();
            Require(Has("settings.usage.unavailable") && !Has("settings.usage.total") && !Has("settings.usage.history"),
                "Successfully empty range was falsely labeled known zero or showed secondary panels");
            store.Usage["codex"] = local with { UpdatedAt = null }; store.Events["codex"] = events;
            store.RecordLocalAnalyticsRead("codex", through); pane.RefreshReadings(); await Idle(); Total(1100);
            var disabledHistory = Descendants<Expander>(Find("settings.usage.history")).ToArray();
            Require(disabledHistory.Length == 4, "Null UpdatedAt must preserve exactly four calendar history rows");
            Require(disabledHistory.All(x => !x.IsEnabled), "Unattributed calendar history remained interactive without UpdatedAt");
            Require(clock.Reads == 0, "A state transition reintroduced render-clock source time");
            checks.Add("Publication-bound pending midnight/failure/partial data retention with single visible status priority, unqueried range isolation, recovery, focus cannot defer maintenance epoch revocation, loading, source-empty Unavailable and exactly four disabled unknown-time history rows");
            // Overview requires a real today analytics source, even if retained
            // calendar totals remain healthy while maintenance has cleared that source.
            clock.Now = through.AddDays(10);
            var priced = Event("overview-cost", through.AddSeconds(-1), 100000, "gpt-5.6-sol", "overview-cost", 20000, 20000);
            var pricedLocal = new UsageSnapshot(priced.Usage, priced.Usage, priced.Usage, priced.Usage, DataQuality.Exact, priced.OccurredAt);
            UsageEvent[] pricedEvents = [priced];
            store.Events["codex"] = pricedEvents; store.Usage["codex"] = pricedLocal;
            store.RecordLocalAnalyticsRead("codex", through);
            settings.Save(settings.Current with { CostEstimatesEnabled = true });
            // Setting IsChecked raises the actual production WPF Checked route.
            Radio("settings.usage.tab.overview").IsChecked = true; await Idle();
            void RetainedToday()
            {
                Require(ReferenceEquals(store.Usage["codex"], pricedLocal) && ReferenceEquals(store.Events["codex"], pricedEvents)
                    && store.Usage["codex"].UpdatedAt == priced.OccurredAt && store.Usage["codex"].Quality == DataQuality.Exact,
                    "Overview maintenance fixture changed its otherwise retained healthy local data");
                Require(Descendants<AnimatedMetric>(Find("usage.overview")).Single(x => x.FontSize == 42).DisplayedValue == 120000,
                    "Overview source revocation changed the retained Today token total");
            }
            void PositiveOverviewCost()
            {
                // Bundled rates: (80000*4 + 20000*0.4 + 20000*20)/1e6 = 0.728 USD.
                Require(Text(Find("usage.today.cost")) == "Estimated API cost · ~$" + 0.728m.ToString("N2", CultureInfo.CurrentCulture),
                    "Trusted published Overview source did not show its positive expected API estimate");
                RetainedToday();
            }
            PositiveOverviewCost();
            Require(clock.Reads == 0, "Overview read a render clock despite having a trusted source Through");
            store.RebuildingProviders.Add("codex");
            store.LocalAnalyticsEpochs["codex"] = store.LocalAnalyticsEpochs.GetValueOrDefault("codex") + 1;
            store.AnalyticsSources.Remove("codex"); pane.RefreshReadings(); await Idle();
            RetainedToday();
            Require(!Has("usage.today.cost"), "Maintenance source absence fabricated a zero-dollar estimate beside retained nonzero tokens");
            store.RebuildingProviders.Remove("codex");
            store.DataOperationMessages["codex"] = "Synthetic rebuild failed. Existing statistics were retained.";
            pane.RefreshReadings(); await Idle();
            RetainedToday();
            Require(!Has("usage.today.cost") && !store.AnalyticsSources.ContainsKey("codex"),
                "Failed maintenance without a new source restored an unavailable Overview estimate");
            store.RecordLocalAnalyticsRead("codex", through.AddSeconds(10)); pane.RefreshReadings(); await Idle();
            PositiveOverviewCost();
            Require(clock.Reads == 0, "Overview source absence used the render clock to fabricate cost coverage");
            checks.Add("Overview positive published estimate, source/epoch revocation with retained Exact Today120000, failed-maintenance cost absence, successful publication recovery and no render-clock fallback");
            File.WriteAllText(Path.Combine(directory, "windows-settings-analytics.json"), JsonSerializer.Serialize(new
            {
                completed = true, checks, sourceThrough = through, localUpdatedAt = yesterday, clockReads = clock.Reads,
                input = "synthetic WPF routed events", physicalInputVerified = false, liveAccountsVerified = false,
                defensiveExactZero = "Not source-reachable: Build(empty) is Unavailable; no fabricated successful zero publication",
                maintenance = "UI/cache epoch projection verified; actual I/O race ordering requires Store tests/source review"
            }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => window?.Close());
            Restore(() => { if (originalUsage is null) store.Usage.Remove("codex"); else store.Usage["codex"] = originalUsage; });
            Restore(() => { if (originalEvents is null) store.Events.Remove("codex"); else store.Events["codex"] = originalEvents; });
            Restore(() => { if (originalOperationMessage is null) store.DataOperationMessages.Remove("codex"); else store.DataOperationMessages["codex"] = originalOperationMessage; });
            Restore(() => { if (wasRebuilding) store.RebuildingProviders.Add("codex"); else store.RebuildingProviders.Remove("codex"); });
            Restore(publicationState.Dispose);
            Restore(() => settings.Save(before));
        }
        if (cleanup.Count > 0) throw new AggregateException("Settings analytics fixture cleanup failed", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
