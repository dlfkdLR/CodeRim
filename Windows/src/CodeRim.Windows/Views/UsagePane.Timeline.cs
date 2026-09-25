using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Views;
internal sealed partial class UsagePane
{
    private static void AdaptHeader(Grid grid, FrameworkElement actions)
    {
        AutomationProperties.SetAutomationId(grid, "usage.header");
        AutomationProperties.SetAutomationId(actions, "usage.controls");
        bool? compact = null;
        void Layout()
        {
            var detail = !grid.Children.OfType<System.Windows.Controls.ComboBox>().Any(x => x.Visibility == Visibility.Visible);
            var next = grid.ActualWidth < 480;
            if (detail) { compact = null; grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear(); Grid.SetRow(actions, 0); Grid.SetColumn(actions, 0); actions.Margin = new Thickness(0); return; }
            if (compact == next) return;
            compact = next; grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (next) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            else grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(actions, next ? 1 : 0); Grid.SetColumn(actions, next ? 0 : 1);
            actions.Margin = next ? new Thickness(0, 12, 0, 0) : new Thickness(12, 0, 0, 0);
        }
        grid.SizeChanged += (_, _) => Layout();
        foreach (var picker in grid.Children.OfType<System.Windows.Controls.ComboBox>()) picker.IsVisibleChanged += (_, _) => Layout();
    }
    private static void AdaptOverview(Grid grid, FrameworkElement total, FrameworkElement breakdown)
    {
        bool? compact = null;
        grid.SizeChanged += (_, _) =>
        {
            var next = grid.ActualWidth < 492;
            if (compact == next) return;
            compact = next; grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            if (next) { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
            else grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            Grid.SetColumn(breakdown, next ? 0 : 1); Grid.SetRow(breakdown, next ? 1 : 0);
            total.Margin = next ? new Thickness(0, 0, 0, 16) : new Thickness(0, 0, 32, 0);
        };
    }
    private static void AdaptHistory(Grid grid)
    {
        bool? compact = null;
        var buttons = grid.Children.OfType<Button>().ToArray();
        grid.SizeChanged += (_, _) =>
        {
            var next = grid.ActualWidth < 470;
            if (compact == next) return;
            compact = next; grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
            for (var i = 0; i < (next ? 1 : buttons.Length); i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < (next ? buttons.Length : 1); i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < buttons.Length; i++)
            {
                Grid.SetColumn(buttons[i], next ? 0 : i); Grid.SetRow(buttons[i], next ? i : 0);
                buttons[i].Padding = next ? new Thickness(0, 8, 0, 8) : new Thickness(i == 0 ? 0 : 16, 4, 16, 4);
            }
            foreach (var separator in grid.Children.OfType<Border>()) separator.Visibility = next ? Visibility.Collapsed : Visibility.Visible;
        };
    }
    private string? selectedModel;
    private DateTimeOffset? selectedBucket;
    private void Timeline(UsageEvent[] events, CostSummary totalCost)
    {
        var now = DateTimeOffset.Now;
        var showsCost = settings.Current.CostEstimatesEnabled && provider == "codex";
        var excludedModels = totalCost.ExcludedModels.ToHashSet(StringComparer.Ordinal);
        var range = period switch { "today" => AnalyticsRange.Today, "30d" => AnalyticsRange.ThirtyDays, _ => AnalyticsRange.SevenDays };
        var buckets = period is "today" or "7d" or "30d"
            ? AnalyticsTimeline.Build(events, range, now, TimeZoneInfo.Local)
            : events.GroupBy(e => e.OccurredAt.LocalDateTime.Date).OrderBy(g => g.Key).TakeLast(30)
                .Select(g => new AnalyticsBucket(new DateTimeOffset(g.Key), new DateTimeOffset(g.Key.AddDays(1)),
                    g.Aggregate(TokenUsage.Zero, (sum, e) => sum.Add(e.Usage)), UsageAnalytics.Estimate(g, excludedModels))).ToArray();
        var details = new StackPanel { Margin = new Thickness(10) };
        var detailCard = new Border { Child = details, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 12, 0, 12), Visibility = Visibility.Collapsed };
        detailCard.SetResourceReference(Border.BackgroundProperty, "LimitCardBackground");
        void Select(AnalyticsBucket bucket)
        {
            selectedBucket = bucket.Start; details.Children.Clear();
            detailCard.Visibility = Visibility.Visible;
            details.Children.Add(Ui.Text(BucketLabel(bucket.Start), 11, weight: FontWeights.SemiBold));
            AnalyticsSummary(details, bucket.Usage, bucket.Cost, compact: true);
            AnalyticsBreakdown(details, bucket.Usage);
            AutomationProperties.SetName(details, "Selected usage interval");
            AutomationProperties.SetAutomationId(details, "usage.bucket-details");
        }
        void Chart(bool cost)
        {
            var title = cost ? totalCost.IsPartial ? "Estimated cost of priced usage" : "Estimated API cost" : "Token activity";
            AnalyticsHeading(readings, title);
            if (cost && totalCost.Amount is null)
            {
                readings.Children.Add(Ui.Text("No cost estimate is available for the recorded usage in this range.", 11, "#A6A6AA"));
                return;
            }
            var chart = new Grid { Height = showsCost ? 80 : 112, Margin = new Thickness(0, 8, 0, 2) };
            AutomationProperties.SetName(chart, title + " by " + (period == "today" ? "hour" : "day"));
            var values = buckets.Select(b => cost ? b.Cost.Amount.HasValue ? (double?)b.Cost.Amount.Value : null : b.Usage.TotalTokens).ToArray();
            var maximum = values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();
            for (var i = 0; i < buckets.Count; i++)
            {
                var bucket = buckets[i]; var value = values[i]; chart.ColumnDefinitions.Add(new ColumnDefinition());
                var label = BucketLabel(bucket.Start) + ": " + (cost ? CostText(bucket.Cost) : bucket.Usage.TotalTokens.ToString("N0", CultureInfo.CurrentCulture) + " tokens");
                var fill = new Border { Height = BarHeight(value, maximum, chart.Height - 16),
                    VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(3) };
                fill.SetResourceReference(Border.BackgroundProperty, cost ? "UsageAmple" : "AccentBrush");
                var content = new Grid(); content.Children.Add(fill);
                var button = Ui.Button("", () => Select(bucket)); button.Content = content;
                button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); button.Padding = new Thickness(1);
                button.Margin = new Thickness(1); button.MinWidth = 0;
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Stretch;
                button.ToolTip = label; AutomationProperties.SetName(button, label);
                AutomationProperties.SetAutomationId(button, "usage.bucket." + (cost ? "cost." : "tokens.") + bucket.Start.ToUnixTimeSeconds());
                Grid.SetColumn(button, i); chart.Children.Add(button);
            }
            readings.Children.Add(chart);
            if (cost && values.Any(v => !v.HasValue))
                readings.Children.Add(Ui.Text("Gaps indicate intervals without a cost estimate.", 11, "#A6A6AA"));
            if (buckets.Count > 0)
                readings.Children.Add(Ui.Row(BucketLabel(buckets[0].Start), BucketLabel(buckets[^1].Start)));
        }
        Chart(false);
        if (showsCost) Chart(true);
        if (period is not ("today" or "7d" or "30d")) readings.Children.Add(Ui.Text("Chart shows up to 30 recent active days in this period.", 11, "#A6A6AA"));
        readings.Children.Add(detailCard);
        if (selectedBucket is { } selected && buckets.FirstOrDefault(b => b.Start == selected) is { } current) Select(current);
        AnalyticsHeading(readings, "Models");
        foreach (var row in UsageAnalytics.Group(events, "model"))
        {
            var button = Ui.Button("", () => Forward("model", selectedProject: project, selectedSession: session, model: row.Name));
            var costText = showsCost && row.Cost is { } amount ? "~" + AnalyticsCurrency(amount) + (row.Partial ? " · subtotal" : "") : "";
            var content = new DockPanel();
            var arrow = Ui.Text("›", 11, "#98989D"); arrow.Margin = new Thickness(10, 0, 0, 0); arrow.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(arrow, Dock.Right); content.Children.Add(arrow);
            var values = new StackPanel();
            var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = Ui.Text(row.Name, 13, weight: FontWeights.Medium); name.Margin = new Thickness(0, 0, 8, 0);
            name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = row.Name; top.Children.Add(name);
            var tokens = Ui.Text(row.Tokens.ToString("N0", CultureInfo.CurrentCulture)); tokens.Margin = new Thickness(0);
            System.Windows.Documents.Typography.SetNumeralAlignment(tokens, FontNumeralAlignment.Tabular);
            Grid.SetColumn(tokens, 1); top.Children.Add(tokens); values.Children.Add(top);
            if (costText.Length > 0)
            { var estimate = Ui.Text(costText, 11, "#A6A6AA"); estimate.Margin = new Thickness(0, 4, 0, 0); estimate.HorizontalAlignment = HorizontalAlignment.Right; values.Children.Add(estimate); }
            content.Children.Add(values); button.Content = content;
            button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); button.Margin = new Thickness(0); button.Padding = new Thickness(8, 10, 8, 10);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetName(button, row.Name + ": " + row.Tokens.ToString("N0", CultureInfo.CurrentCulture) + " tokens" + (costText.Length == 0 ? "" : ", " + costText));
            AutomationProperties.SetAutomationId(button, "usage.model." + row.Name);
            readings.Children.Add(button);
        }
    }
    internal static double BarHeight(double? value, double maximum, double height = 96) => value is > 0 && maximum > 0
        ? Math.Max(2, Math.Min(1, value.Value / maximum) * height) : 0;
    private void MetricSummary(Panel parent, TokenUsage tokens)
    {
        parent.Children.Add(Ui.Row("Total tokens", TokenFormatter.Format(tokens.TotalTokens, settings.Current.NumberStyle)));
        Breakdown(parent, tokens);
    }
    private string BucketLabel(DateTimeOffset date) => period == "today"
        ? date.ToLocalTime().ToString("HH:mm zzz", CultureInfo.CurrentCulture)
        : CalendarDateText.MonthDay(date.LocalDateTime) ?? "Date unavailable";
    private static string CostText(CostSummary cost) => cost.Amount is { } amount
        ? "$" + amount.ToString("N4", CultureInfo.CurrentCulture) + (cost.IsPartial ? " · partial" : "") : "Unavailable";
}
