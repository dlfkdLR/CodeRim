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
        var wasRefreshing = store.RefreshingProviders.Contains("codex");
        Window? window = null; Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            settings.Save(settings.Current with { CostEstimatesEnabled = true, UsageProvider = "codex" });
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
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "windows-analytics-states.json"), System.Text.Json.JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Pending without numbers and motion cleanup", "Empty range and selected interval without invented cost", "Older data recovers on range change",
                    "Focused refresh retains partial snapshot with stale warning", "Model and activity errors independent of quota refresh", "Zero-valued model coverage", "Recovery and clear" } }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { window?.Close(); } catch (Exception error) { cleanup.Add(error); }
            try { if (previousUsage is null) store.Usage.Remove("codex"); else store.Usage["codex"] = previousUsage; } catch (Exception error) { cleanup.Add(error); }
            try { if (previousEvents is null) store.Events.Remove("codex"); else store.Events["codex"] = previousEvents; } catch (Exception error) { cleanup.Add(error); }
            try { if (!wasRefreshing) store.RefreshingProviders.Remove("codex"); } catch (Exception error) { cleanup.Add(error); }
            try { settings.Save(settings.Current with { CostEstimatesEnabled = previousCost, UsageProvider = previousProvider }); } catch (Exception error) { cleanup.Add(error); }
        }
        if (failure is not null && cleanup.Count > 0) throw new AggregateException("Analytics state fixture and cleanup failed", cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanup.Count > 0) throw new AggregateException("Analytics state fixture cleanup failed", cleanup);
    }
}
