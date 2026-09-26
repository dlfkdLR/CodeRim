using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

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
    private readonly Dictionary<string, Button> timelineButtons = new(StringComparer.Ordinal);
    private void Timeline(UsageEvent[] events, CostSummary totalCost, DateTimeOffset through, DataQuality? quality = null)
    {
        var showsCost = settings.Current.CostEstimatesEnabled && provider == "codex";
        var excludedModels = totalCost.ExcludedModels.ToHashSet(StringComparer.Ordinal);
        var range = period switch { "today" => AnalyticsRange.Today, "30d" => AnalyticsRange.ThirtyDays, _ => AnalyticsRange.SevenDays };
        var buckets = period is "today" or "7d" or "30d"
            ? AnalyticsTimeline.Build(events, range, through, TimeZoneInfo.Local)
            : events.GroupBy(e => e.OccurredAt.LocalDateTime.Date).OrderBy(g => g.Key).TakeLast(30)
                .Select(g => new AnalyticsBucket(new DateTimeOffset(g.Key), new DateTimeOffset(g.Key.AddDays(1)),
                    g.Aggregate(TokenUsage.Zero, (sum, e) => sum.Add(e.Usage)), UsageAnalytics.Estimate(g, excludedModels))).ToArray();
        if (quality == DataQuality.Unavailable) buckets = buckets.Select(bucket => bucket with { Cost = new CostSummary(null, 0, []) }).ToArray();
        var domainStart = AnalyticsTimeline.Start(range, through, TimeZoneInfo.Local);
        var details = new StackPanel { Margin = new Thickness(10) };
        var detailCard = new Border { Child = details, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 12, 0, 12), Visibility = Visibility.Collapsed };
        detailCard.SetResourceReference(Border.BackgroundProperty, "LimitCardBackground");
        void Select(AnalyticsBucket bucket, DateTimeOffset? rawSelection = null)
        {
            selectedBucket = rawSelection ?? bucket.Start; details.Children.Clear();
            if (destination == "activity") analyticsSelectedBucket = selectedBucket;
            detailCard.Visibility = Visibility.Visible;
            details.Children.Add(Ui.Text(AnalyticsDateText.Format(bucket.Start, period == "today" ? AnalyticsDateStyle.Time : AnalyticsDateStyle.Day), 11, weight: FontWeights.SemiBold));
            AnalyticsSummary(details, bucket.Usage, bucket.Cost, compact: true, quality);
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
            // Swift Charts' frame includes the X axis. Keep labels inside the
            // same 80/112-point frame instead of appending another content row.
            var axisHeight = domainStart == through ? 0d : 18d;
            var chart = new Grid { Height = showsCost ? 80 : 112 };
            chart.RowDefinitions.Add(new RowDefinition());
            chart.RowDefinitions.Add(new RowDefinition { Height = new GridLength(axisHeight) });
            AutomationProperties.SetName(chart, title + " by " + (period == "today" ? "hour" : "day"));
            var values = buckets.Select(b => cost ? b.Cost.Amount.HasValue ? (double?)b.Cost.Amount.Value : null : b.Usage.TotalTokens).ToArray();
            var maximum = values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();
            var marks = new List<(Button Button, Border Fill, DateTimeOffset Start)>();
            for (var i = 0; i < buckets.Count; i++)
            {
                var bucket = buckets[i]; var value = values[i];
                var label = BucketLabel(bucket.Start) + ": " + (cost ? CostText(bucket.Cost) : bucket.Usage.TotalTokens.ToString("N0", CultureInfo.CurrentCulture) + " tokens");
                var fill = new Border { Width = 8, Height = BarHeight(value, maximum, chart.Height - axisHeight),
                    VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left,
                    IsHitTestVisible = false, CornerRadius = new CornerRadius(3) };
                fill.SetResourceReference(Border.BackgroundProperty, cost ? "UsageAmple" : "AccentBrush");
                var content = new Grid(); content.Children.Add(fill);
                var button = Ui.Button("", () => Select(bucket)); button.Content = content; button.Tag = bucket.Start;
                button.Template = ChartBarTemplate(); Motion.SetFeedback(button, false);
                button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); button.Padding = new Thickness(0);
                button.Margin = new Thickness(0); button.MinWidth = 0; button.MinHeight = 0;
                button.HorizontalAlignment = HorizontalAlignment.Left; button.Cursor = System.Windows.Input.Cursors.Arrow;
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Stretch;
                button.ToolTip = label; AutomationProperties.SetName(button, label);
                AutomationProperties.SetAutomationId(button, "usage.bucket." + (cost ? "cost." : "tokens.") + bucket.Start.ToUnixTimeSeconds());
                timelineButtons[AutomationProperties.GetAutomationId(button)] = button;
                chart.Children.Add(button);
                marks.Add((button, fill, bucket.Start));
            }
            chart.SizeChanged += (_, _) =>
            {
                var centers = marks.Select(mark => AnalyticsTimeline.Position(mark.Start, domainStart, through, chart.ActualWidth)).ToArray();
                for (var i = 0; i < marks.Count; i++)
                {
                    // Hit regions end halfway between dates. Fixed-width hit boxes
                    // overlap in narrow 30D charts and can select the next interval.
                    var left = i == 0 ? 0 : (centers[i - 1] + centers[i]) / 2;
                    var right = i == marks.Count - 1 ? chart.ActualWidth : (centers[i] + centers[i + 1]) / 2;
                    marks[i].Button.Width = Math.Max(0, right - left);
                    marks[i].Button.Margin = new Thickness(left, 0, 0, 0);
                    marks[i].Fill.Margin = new Thickness(centers[i] - left - 4, 0, 0, 0);
                }
            };
            if (buckets.Count > 0 && axisHeight > 0)
            {
                var axis = new Grid();
                AutomationProperties.SetAutomationId(axis, "usage.chart.axis." + (cost ? "cost" : "tokens"));
                axis.ColumnDefinitions.Add(new ColumnDefinition()); axis.ColumnDefinitions.Add(new ColumnDefinition());
                var first = Ui.Text(BucketLabel(buckets[0].Start), 11, "#A6A6AA"); first.Margin = new Thickness(0);
                var last = Ui.Text(BucketLabel(buckets[^1].Start), 11, "#A6A6AA"); last.Margin = new Thickness(0);
                first.VerticalAlignment = last.VerticalAlignment = VerticalAlignment.Bottom;
                first.TextWrapping = last.TextWrapping = TextWrapping.NoWrap;
                first.TextTrimming = last.TextTrimming = TextTrimming.CharacterEllipsis;
                last.TextAlignment = TextAlignment.Right;
                axis.Children.Add(first);
                // A single hour has one tick, not duplicate labels at both ends.
                if (buckets.Count > 1) { Grid.SetColumn(last, 1); axis.Children.Add(last); }
                Grid.SetRow(axis, 1); chart.Children.Add(axis);
            }
            readings.Children.Add(chart);
            if (cost && values.Any(v => !v.HasValue))
                readings.Children.Add(Ui.Text("Gaps indicate intervals without a cost estimate.", 11, "#A6A6AA"));
        }
        Chart(false);
        if (showsCost) Chart(true);
        if (period is not ("today" or "7d" or "30d")) readings.Children.Add(Ui.Text("Chart shows up to 30 recent active days in this period.", 11, "#A6A6AA"));
        readings.Children.Add(detailCard);
        if (selectedBucket is { } selected && AnalyticsTimeline.Nearest(buckets, selected) is { } current) Select(current, selected);
        AnalyticsHeading(readings, "Models");
        if (events.Length == 0) readings.Children.Add(Ui.Text("No model-tagged usage in this range.", 11, "#A6A6AA"));
        foreach (var row in UsageAnalytics.Group(events, "model"))
        {
            var costText = showsCost && quality != DataQuality.Unavailable && row.Cost is { } amount ? "~" + AnalyticsCurrency(amount) + (row.Partial ? " · subtotal" : "") : "";
            readings.Children.Add(AnalyticsRowButton(row.Name, null, row.Tokens, costText, "usage.model." + row.Name, 8,
                () => Forward("model", selectedProject: project, selectedSession: session, model: row.Name)));
        }
    }
    internal static double BarHeight(double? value, double maximum, double height = 96) => value is > 0 && maximum > 0
        ? Math.Min(1, value.Value / maximum) * height : 0;
    private static ControlTemplate ChartBarTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        root.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var focus = new FrameworkElementFactory(typeof(Border), "Focus");
        focus.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        focus.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        focus.SetValue(UIElement.IsHitTestVisibleProperty, false);
        root.AppendChild(focus);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = root };
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentBrush"), "Focus"));
        template.Triggers.Add(focused);
        return template;
    }
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
