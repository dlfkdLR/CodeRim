using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class UsagePane
{
    private Border AnalyticsRangeControl()
    {
        var segments = new UniformGrid { Columns = 3 };
        var group = new Border { Child = segments, CornerRadius = new CornerRadius(6), Padding = new Thickness(2), Height = 28 };
        group.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        AutomationProperties.SetName(group, "Usage period"); AutomationProperties.SetAutomationId(group, "usage.range");
        foreach (var (id, title) in new[] { ("today", "Today"), ("7d", "7D"), ("30d", "30D") })
        {
            var choice = new RadioButton { Content = title, Tag = id, IsChecked = period == id, GroupName = "UsageRange",
                Style = (Style)FindResource("UsageModeButton") };
            AutomationProperties.SetName(choice, title); AutomationProperties.SetAutomationId(choice, "usage.range." + id);
            choice.Checked += (_, _) => ChangePeriod(id);
            segments.Children.Add(choice);
        }
        return group;
    }

    private void AnalyticsSummary(Panel parent, TokenUsage tokens, CostSummary cost, bool compact, DataQuality? quality = null)
    {
        var group = new StackPanel();
        var metrics = new Grid(); metrics.ColumnDefinitions.Add(new ColumnDefinition());
        var showsCost = settings.Current.CostEstimatesEnabled && provider == "codex";
        if (showsCost) metrics.ColumnDefinitions.Add(new ColumnDefinition());
        FrameworkElement Value(string value, string label, string id)
        {
            var column = new StackPanel();
            var number = Ui.Text(value, compact ? 18 : 28, weight: FontWeights.SemiBold);
            number.Margin = new Thickness(0); number.TextWrapping = TextWrapping.NoWrap; number.TextTrimming = TextTrimming.CharacterEllipsis;
            System.Windows.Documents.Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
            var fit = new Viewbox { Child = number, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left, MaxHeight = compact ? 26 : 38 };
            // Match the reference's minimumScaleFactor(0.65); unusually long
            // localized numbers truncate instead of becoming unreadably small.
            column.SizeChanged += (_, _) => number.MaxWidth = Math.Max(1, column.ActualWidth / .65);
            column.Children.Add(fit);
            var caption = Ui.Text(label, 11, "#A6A6AA"); caption.Margin = new Thickness(0, 5, 0, 0); column.Children.Add(caption);
            AutomationProperties.SetAutomationId(column, "analytics.summary." + id);
            AutomationProperties.SetName(column, value + " " + label); return column;
        }
        var count = Value(tokens.TotalTokens.ToString("N0", CultureInfo.CurrentCulture), "tokens", "tokens");
        if (showsCost) count.Margin = new Thickness(0, 0, 20, 0);
        metrics.Children.Add(count);
        if (showsCost)
        {
            var estimate = Value(cost.Amount is { } amount ? "~" + AnalyticsCurrency(amount) : "—",
                cost.Amount is null ? "Estimate unavailable" : cost.Label, "cost");
            estimate.ToolTip = "API-equivalent estimate for recorded usage, not a bill or subscription charge. Unpriced models are excluded.";
            Grid.SetColumn(estimate, 1); metrics.Children.Add(estimate);
        }
        group.Children.Add(metrics);
        var snapshot = store.Usage.GetValueOrDefault(provider);
        if ((quality ?? (snapshot?.RetainsPartialHistory == true ? DataQuality.Partial : snapshot?.Quality)) == DataQuality.Partial)
        { var partial = Ui.Text("Partial local history", 11, "#A6A6AA"); partial.Margin = new Thickness(0, 8, 0, 0); group.Children.Add(partial); }
        if (showsCost && cost.IsPartial)
        { var missing = Ui.Text("Pricing unavailable: " + string.Join(", ", cost.ExcludedModels), 11, "#A6A6AA"); missing.Margin = new Thickness(0, 8, 0, 0); group.Children.Add(missing); }
        parent.Children.Add(group);
    }

    private static string AnalyticsCurrency(decimal amount) => "$" + amount.ToString(amount < 1 ? "#,0.00##" : "N2", CultureInfo.CurrentCulture);

    private static void AnalyticsHeading(Panel parent, string title)
    {
        var text = Ui.Text(title, 13, weight: FontWeights.SemiBold); text.Margin = new Thickness(0, 16, 0, 6); parent.Children.Add(text);
    }

    private static void AnalyticsBreakdown(Panel parent, TokenUsage tokens)
    {
        foreach (var (label, value) in new[] { ("Input", tokens.InputTokens), ("Cached input", tokens.CachedInputTokens), ("Output", tokens.OutputTokens) })
        {
            parent.Children.Add(AnalyticsValueRow(label, value, 6));
        }
    }

    private static Grid AnalyticsValueRow(string label, long value, double spacing)
    {
        var row = new Grid { Margin = new Thickness(0, spacing, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Ui.Text(label, 13); title.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(title);
        var number = Ui.Text(value.ToString("N0", CultureInfo.CurrentCulture), 13, "#A6A6AA"); number.Margin = new Thickness(0);
        System.Windows.Documents.Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        Grid.SetColumn(number, 1); row.Children.Add(number); return row;
    }
}
