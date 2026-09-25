using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;
internal static partial class NativeSmoke
{
    private static async Task AnalyticsRegression(DashboardStore store, AppSettingsStore settings, string directory)
    {
        foreach (var width in new[] { 360d, 450d, 600d })
        {
            var pane = new UsagePane(store, settings, "codex", _ => { }) { Width = width };
            pane.Measure(new Size(width, double.PositiveInfinity)); pane.Arrange(new Rect(0, 0, width, pane.DesiredSize.Height));
            pane.UpdateLayout(); await Idle(); pane.UpdateLayout();
            var header = Descendants<Grid>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.header");
            var selector = Descendants<ComboBox>(header).Single(x => AutomationProperties.GetName(x) == "Usage provider");
            var actions = Descendants<StackPanel>(header).Single(x => AutomationProperties.GetAutomationId(x) == "usage.controls");
            var selectorBounds = selector.TransformToAncestor(header).TransformBounds(new Rect(selector.RenderSize));
            var actionsBounds = actions.TransformToAncestor(header).TransformBounds(new Rect(actions.RenderSize));
            Require(!selectorBounds.IntersectsWith(actionsBounds), "Narrow header actions overlap the provider selector");
            var overview = Descendants<Grid>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.overview");
            var history = Descendants<Grid>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.history");
            var links = Descendants<UniformGrid>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.links");
            Require(overview.ColumnDefinitions.Count == (width - 48 < 470 ? 1 : 2), "Narrow usage overview did not reflow");
            Require(history.ColumnDefinitions.Count == (width - 48 < 470 ? 1 : 3), "Narrow usage history did not reflow");
            Require(links.Columns == (width - 48 < 390 ? 1 : 3), "Narrow usage links did not reflow");
            foreach (var host in new Panel[] { overview, history, links })
                foreach (var child in host.Children.OfType<FrameworkElement>().Where(x => x.Visibility == Visibility.Visible))
                {
                    var bounds = child.TransformToAncestor(host).TransformBounds(new Rect(child.RenderSize));
                    Require(bounds.Left >= -1 && bounds.Right <= host.ActualWidth + 1, "Usage child exceeds the narrow content width");
                }
            Capture(pane, System.IO.Path.Combine(directory, "windows-usage-width-" + width.ToString(CultureInfo.InvariantCulture) + ".png"));
        }
        var previousEvents = store.Events.GetValueOrDefault("codex");
        var previousCost = settings.Current.CostEstimatesEnabled;
        Window? analyticsWindow = null;
        Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            settings.Save(settings.Current with { CostEstimatesEnabled = true });
            var now = DateTimeOffset.Now;
            store.Events["codex"] = [
                new("small", now.AddDays(-1), new(100, 0, 0, 0), "gpt-5.6-sol", "Fixture", "session", "codex", "project"),
                new("large", now, new(1000, 0, 0, 0), "gpt-5.6-sol", "Fixture", "session", "codex", "project"),
                new("unknown", now, new(10, 0, 0, 0), "unpriced-model", "Fixture", "session", "codex", "project")];
            var pane = new UsagePane(store, settings, "codex", _ => { }) { Width = 650 };
            analyticsWindow = new Window { Content = pane, Width = 700, Height = 760, Title = "Analytics parity fixture" };
            analyticsWindow.Show(); await Idle();
            Descendants<Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.destination.activity").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle();
            var periods = Descendants<RadioButton>(pane).Where(x => x.GroupName == "UsageRange").ToArray();
            Require(periods.Length == 3 && periods.All(x => x.ActualHeight == 24), "Analytics range is not the reference three-segment control");
            void Period(string id) => periods.Single(x => Equals(x.Tag, id)).IsChecked = true;
            foreach (var (period, total) in new[] { ("today", 1010L), ("7d", 1110L), ("30d", 1110L) })
            {
                Period(period);
                pane.UpdateLayout(); await Idle();
                var summary = Descendants<StackPanel>(pane).First(x => AutomationProperties.GetAutomationId(x) == "analytics.summary.tokens");
                Require(Descendants<TextBlock>(summary).Any(x => x.Text == total.ToString("N0", CultureInfo.CurrentCulture) && x.FontSize == 28), "Analytics range has no exact prominent total");
                var last = Descendants<Button>(pane).Last(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.tokens.", StringComparison.Ordinal));
                last.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                var details = Descendants<StackPanel>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.bucket-details");
                Require(Descendants<TextBlock>(details).Any(x => x.Text == "tokens")
                    && Descendants<TextBlock>(details).Any(x => x.Text == 1010L.ToString("N0", CultureInfo.CurrentCulture) && x.FontSize == 18), "Selected bucket does not show its exact compact total");
            }
            Period("7d");
            var costButtons = Descendants<Button>(pane).Where(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.cost.", StringComparison.Ordinal)).ToArray();
            pane.UpdateLayout(); await Idle();
            var chart = (Grid)costButtons[0].Parent;
            Require(chart.ActualHeight == 80, "Cost-enabled charts lost the reference compact height");
            var bottoms = costButtons.TakeLast(2).Select(x => {
                var fill = Descendants<Border>((Grid)x.Content).Single();
                return fill.TransformToAncestor(chart).Transform(new Point(0, fill.ActualHeight)).Y;
            }).ToArray();
            Require(Math.Abs(bottoms[0] - bottoms[1]) < 1, "Cost bars do not share a common baseline");
            var heights = costButtons.Select(x => Descendants<Border>((Grid)x.Content).Single().Height).ToArray();
            Require(heights[^1] > 60 && heights[^2] > 5 && Math.Abs(heights[^1] / heights[^2] - 10) < 0.01, "Sub-dollar costs lost their ten-to-one bar ratio");
            Require(heights.Take(5).All(x => x == 0), "Measured zero cost intervals were drawn as nonzero usage");
            Require(costButtons.All(x => ((SolidColorBrush)Descendants<Border>((Grid)x.Content).Single().Background).Color == ((SolidColorBrush)pane.FindResource("UsageAmple")).Color),
                "Cost bars do not use the reference green theme resource");
            Require(Descendants<TextBlock>(pane).Any(x => x.Text == "Estimated cost of priced usage")
                && !Descendants<TextBlock>(pane).Any(x => x.Text == "Gaps indicate intervals without a cost estimate."), "Partial heading or measured-empty coverage is incorrect");
            Require(Descendants<Button>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.model.unpriced-model"
                && !AutomationProperties.GetName(x).Contains("Unavailable", StringComparison.Ordinal)), "Unknown model row repeats unavailable pricing rather than leaving it to detail");
            Require(!Descendants<TextBlock>(pane).Any(x => x.Text == "Usage history") && Descendants<TextBlock>(pane).Any(x => x.Text == "Token activity" && x.FontSize == 13),
                "Analytics retained an extra title or oversized chart heading");
            Require(Descendants<Button>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.model.gpt-5.6-sol"
                && AutomationProperties.GetName(x).Contains('$')), "Known model cost is not visible in its row");
            Capture(pane, System.IO.Path.Combine(directory, "windows-analytics-cost.png"));
            Descendants<Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.model.unpriced-model").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Require(Descendants<TextBlock>(pane).Any(x => x.Text == "Estimate unavailable")
                && Descendants<TextBlock>(pane).Any(x => x.Text == "Projects") && Descendants<TextBlock>(pane).Any(x => x.Text == "Sessions")
                && !Descendants<Button>(pane).Any(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.", StringComparison.Ordinal))
                && !Descendants<ComboBox>(pane).Any(x => AutomationProperties.GetName(x) == "Usage period"), "Model detail lost unavailable pricing/counts or retained unrelated chart/period controls");
            Capture(pane, System.IO.Path.Combine(directory, "windows-analytics-model-unpriced.png"));
            pane.Back(); await Idle();
            Require(Descendants<RadioButton>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.range.7d").IsChecked == true,
                "Model Back did not restore the analytics range");
            foreach (var width in new[] { 360d, 450d, 650d })
            {
                pane.Width = width; pane.UpdateLayout(); await Idle();
                var total = Descendants<StackPanel>(pane).First(x => AutomationProperties.GetAutomationId(x) == "analytics.summary.tokens");
                var estimate = Descendants<StackPanel>(pane).First(x => AutomationProperties.GetAutomationId(x) == "analytics.summary.cost");
                var metrics = (Grid)total.Parent;
                var totalBounds = total.TransformToAncestor(metrics).TransformBounds(new Rect(total.RenderSize));
                var costBounds = estimate.TransformToAncestor(metrics).TransformBounds(new Rect(estimate.RenderSize));
                Require(totalBounds.Right + 19 <= costBounds.Left && costBounds.Right <= metrics.ActualWidth + 1,
                    "Analytics metric columns overlap or exceed a narrow content width");
                Capture(pane, System.IO.Path.Combine(directory, "windows-analytics-width-" + width.ToString(CultureInfo.InvariantCulture) + ".png"));
            }
            costButtons = Descendants<Button>(pane).Where(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.cost.", StringComparison.Ordinal)).ToArray();
            costButtons[^1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            var selected = Descendants<StackPanel>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.bucket-details");
            Require(selected.Parent is Border { CornerRadius.TopLeft: 8, Visibility: Visibility.Visible } && selected.Margin.Left == 10,
                "Selected interval lost its inset card");
            settings.Save(settings.Current with { CostEstimatesEnabled = false }); pane.Update(); await Idle();
            var tokenBar = Descendants<Button>(pane).First(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.tokens.", StringComparison.Ordinal));
            Require(((Grid)tokenBar.Parent).ActualHeight == 112 && !Descendants<Button>(pane).Any(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.cost.", StringComparison.Ordinal)),
                "Token-only chart did not restore its full height");
            settings.Save(settings.Current with { CostEstimatesEnabled = true });
            store.Events["codex"] = [
                new("complete", now.AddDays(-1), new(1000, 0, 0, 0), "gpt-5.6-sol"),
                new("missing", now, new(2000, 0, 0), "gpt-5.6-sol"),
                new("other", now, new(1000, 0, 0, 0), "gpt-6-astra")];
            pane.Update(); await Idle();
            costButtons = Descendants<Button>(pane).Where(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.cost.", StringComparison.Ordinal)).ToArray();
            Require(Descendants<Border>((Grid)costButtons[^2].Content).Single().Height == 0 && AutomationProperties.GetName(costButtons[^2]).Contains("Unavailable", StringComparison.Ordinal),
                "A model excluded from the range subtotal remained in an earlier cost bar");
            Require(Descendants<TextBlock>(pane).Any(x => x.Text == "Gaps indicate intervals without a cost estimate."), "An actual coverage gap has no explanation");
            Capture(pane, System.IO.Path.Combine(directory, "windows-analytics-cost-coverage.png"));
            store.Events["codex"] = [new("unknown-only", now, new(10, 0, 0, 0), "unpriced-model")];
            pane.Update(); await Idle();
            Require(!Descendants<Button>(pane).Any(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.cost.", StringComparison.Ordinal))
                && Descendants<TextBlock>(pane).Any(x => x.Text == "No cost estimate is available for the recorded usage in this range."), "Unavailable cost range renders an empty chart instead of its explanation");
            Capture(pane, System.IO.Path.Combine(directory, "windows-analytics-cost-unavailable.png"));
            store.Events["codex"] = [new("large-unpriced", now, new(long.MaxValue, 0, 0, 0), "unpriced-model")];
            pane.Width = 360; pane.Update(); await Idle();
            Descendants<Button>(pane).Last(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.tokens.", StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            var largeSummaries = Descendants<StackPanel>(pane).Where(x => AutomationProperties.GetAutomationId(x) == "analytics.summary.tokens").ToArray();
            Require(largeSummaries.Length == 2 && largeSummaries.Any(x => Descendants<TextBlock>(x).Any(t => t.FontSize == 28))
                && largeSummaries.Any(x => Descendants<TextBlock>(x).Any(t => t.FontSize == 18)), "Large-number check requires both range and selected interval summaries");
            foreach (var summary in largeSummaries)
            {
                var number = Descendants<TextBlock>(summary).Single(x => x.Text == long.MaxValue.ToString("N0", CultureInfo.CurrentCulture));
                var bounds = number.TransformToAncestor(summary).TransformBounds(new Rect(number.RenderSize));
                Require(bounds.Right <= summary.ActualWidth + 1 && bounds.Width / number.ActualWidth >= .649,
                    "Large analytics number overflows or shrinks below the reference minimum scale");
            }
            Capture(pane, System.IO.Path.Combine(directory, "windows-analytics-large-number.png"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "windows-analytics-cost.json"), System.Text.Json.JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Mounted Today/7d/30d totals and selected values", "Common baseline and ten-to-one fractional cost ratio", "Reference compact/full chart heights and green costs",
                    "Measured empty intervals are zero, unpriced coverage is a gap", "Model-wide range/bucket exclusions", "Selected interval card", "Unavailable cost explanation",
                    "Three-segment range and exact summary", "Model detail and Back", "Two-column summary at 360/450/650 widths", "Long-number bounds and 0.65 minimum scale" } }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { analyticsWindow?.Close(); } catch (Exception error) { cleanup.Add(error); }
            try { if (previousEvents is null) store.Events.Remove("codex"); else store.Events["codex"] = previousEvents; } catch (Exception error) { cleanup.Add(error); }
            try { settings.Save(settings.Current with { CostEstimatesEnabled = previousCost }); } catch (Exception error) { cleanup.Add(error); }
        }
        if (failure is not null && cleanup.Count > 0) throw new AggregateException("Analytics fixture and cleanup failed", cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanup.Count > 0) throw new AggregateException("Analytics fixture cleanup failed", cleanup);
    }
}
