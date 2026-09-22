using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        try
        {
            settings.Save(settings.Current with { CostEstimatesEnabled = true });
            var now = DateTimeOffset.Now;
            store.Events["codex"] = [
                new("small", now.AddDays(-1), new(100, 0, 0, 0), "gpt-5.6-sol", "Fixture", "session", "codex", "project"),
                new("large", now, new(1000, 0, 0, 0), "gpt-5.6-sol", "Fixture", "session", "codex", "project"),
                new("unknown", now, new(10, 0, 0, 0), "unpriced-model", "Fixture", "session", "codex", "project")];
            var pane = new UsagePane(store, settings, "codex", _ => { }) { Width = 650 };
            Descendants<Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.destination.activity").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var periods = Descendants<ComboBox>(pane).Single(x => AutomationProperties.GetName(x) == "Usage period");
            foreach (var (period, total) in new[] { ("today", 1010L), ("7d", 1110L), ("30d", 1110L) })
            {
                periods.SelectedValue = period;
                pane.Measure(new Size(650, double.PositiveInfinity)); pane.Arrange(new Rect(0, 0, 650, pane.DesiredSize.Height)); pane.UpdateLayout(); await Idle();
                Require(Descendants<TextBlock>(pane).Any(x => x.Text == "Total tokens"), "Analytics range has no visible total");
                var last = Descendants<Button>(pane).Last(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.tokens.", StringComparison.Ordinal));
                last.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                var details = Descendants<StackPanel>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.bucket-details");
                Require(Descendants<TextBlock>(details).Any(x => x.Text == "Total tokens")
                    && Descendants<TextBlock>(details).Any(x => x.Text == CodeRim.Core.Services.TokenFormatter.Format(1010, settings.Current.NumberStyle)), "Selected bucket does not show its expected total");
                Require(Descendants<TextBlock>(pane).Any(x => x.Text == CodeRim.Core.Services.TokenFormatter.Format(total, settings.Current.NumberStyle)), "Analytics period total is incorrect");
            }
            periods.SelectedValue = "7d";
            var costButtons = Descendants<Button>(pane).Where(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.cost.", StringComparison.Ordinal)).ToArray();
            pane.Measure(new Size(650, double.PositiveInfinity)); pane.Arrange(new Rect(0, 0, 650, pane.DesiredSize.Height)); pane.UpdateLayout(); await Idle();
            var chart = (Grid)costButtons[0].Parent;
            var bottoms = costButtons.TakeLast(2).Select(x => {
                var fill = Descendants<Border>((Grid)x.Content).Single();
                return fill.TransformToAncestor(chart).Transform(new Point(0, fill.ActualHeight)).Y;
            }).ToArray();
            Require(Math.Abs(bottoms[0] - bottoms[1]) < 1, "Cost bars do not share a common baseline");
            var heights = costButtons.Select(x => Descendants<Border>((Grid)x.Content).Single().Height).ToArray();
            Require(heights[^1] > 80 && heights[^2] > 5 && Math.Abs(heights[^1] / heights[^2] - 10) < 0.01, "Sub-dollar costs lost their ten-to-one bar ratio");
            Require(heights.Take(5).All(x => x == 0), "Unavailable cost intervals were drawn as known usage");
            Require(Descendants<Button>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.model.unpriced-model"
                && AutomationProperties.GetName(x).Contains("Unavailable", StringComparison.Ordinal)), "Unknown model cost is not visible in its row");
            Require(Descendants<Button>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.model.gpt-5.6-sol"
                && AutomationProperties.GetName(x).Contains('$')), "Known model cost is not visible in its row");
            Capture(pane, System.IO.Path.Combine(directory, "windows-analytics-cost.png"));
        }
        finally
        {
            if (previousEvents is null) store.Events.Remove("codex"); else store.Events["codex"] = previousEvents;
            settings.Save(settings.Current with { CostEstimatesEnabled = previousCost });
        }
    }
}
