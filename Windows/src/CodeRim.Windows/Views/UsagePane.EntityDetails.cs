using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class UsagePane
{
    private void EntityDetail(UsageEvent[] events, DateTimeOffset through)
    {
        if (events.Length == 0)
        {
            readings.Children.Add(Ui.Text("No local usage observed for this period.", 11, "#A6A6AA")); return;
        }
        var allEvents = Filter(through, includeSelection: false).ToArray();
        var total = events.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage));
        var quality = AnalyticsQuality(allEvents.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage)));
        var content = new StackPanel();
        AutomationProperties.SetAutomationId(content, "usage.entity.detail"); readings.Children.Add(content);
        void Add(FrameworkElement child)
        {
            child.Margin = new Thickness(0, content.Children.Count == 0 ? 0 : 14, 0, 0); content.Children.Add(child);
        }
        var header = new StackPanel();
        var title = Ui.Text(AnalyticsEntityName(events, session ?? project!, session is not null), 13, weight: FontWeights.SemiBold);
        title.Margin = new Thickness(0); header.Children.Add(title);
        AutomationProperties.SetAutomationId(title, "usage.entity.title");
        var number = Ui.Text(total.TotalTokens.ToString("N0", CultureInfo.CurrentCulture), 28, weight: FontWeights.SemiBold);
        number.Margin = new Thickness(0); number.TextWrapping = TextWrapping.NoWrap; number.TextTrimming = TextTrimming.CharacterEllipsis;
        System.Windows.Documents.Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        var fit = new Viewbox { Child = number, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left, MaxHeight = 38, Margin = new Thickness(0, 3, 0, 0) };
        header.SizeChanged += (_, _) => number.MaxWidth = Math.Max(1, header.ActualWidth / .75);
        header.Children.Add(fit);
        var caption = Ui.Text("tokens", 11, "#A6A6AA"); caption.Margin = new Thickness(0, 3, 0, 0); header.Children.Add(caption);
        AutomationProperties.SetAutomationId(header, "usage.entity.header"); Add(header);
        if (settings.Current.CostEstimatesEnabled && provider == "codex")
        {
            var cost = quality == DataQuality.Unavailable ? new CostSummary(null, 0, []) : UsageAnalytics.Estimate(events);
            var summary = new StackPanel();
            var estimate = Ui.Text(cost.Amount is { } amount
                ? cost.Label + " · ~" + AnalyticsCurrency(amount) + (quality == DataQuality.Partial ? " · partial history" : "")
                : "Estimated cost unavailable", 11, "#A6A6AA");
            estimate.Margin = new Thickness(0); summary.Children.Add(estimate);
            if (cost.Amount is not null && cost.IsPartial)
            {
                var excluded = Ui.Text("Excludes " + cost.ExcludedTokens.ToString("N0", CultureInfo.CurrentCulture) + " tokens · " + string.Join(", ", cost.ExcludedModels), 11, "#A6A6AA");
                excluded.Margin = new Thickness(0, 3, 0, 0); summary.Children.Add(excluded);
            }
            summary.ToolTip = "API-equivalent estimate for recorded usage, not a bill or subscription charge. Unpriced models are excluded.";
            AutomationProperties.SetAutomationId(summary, "usage.entity.cost"); Add(summary);
        }
        if (session is null) Add(AnalyticsValueRow("Sessions", events.Select(x => x.SessionId).Distinct(StringComparer.Ordinal).LongCount(), 0));
        else
        {
            Add(EntityTextRow("Last activity", AnalyticsDateText.Format(events.Max(x => x.OccurredAt), AnalyticsDateStyle.DayAndTime)));
            var metadata = store.SessionDetails.GetValueOrDefault(provider) ?? [];
            var detail = metadata.FirstOrDefault(x => x.Id == session);
            if (detail?.StartedAt is { } started) Add(EntityTextRow("Started", AnalyticsDateText.Format(started, AnalyticsDateStyle.DayAndTime)));
            if (settings.Current.AgentDetailsEnabled)
            {
                var childIds = metadata.Where(x => x.ParentId == session).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                var children = allEvents.Where(x => childIds.Contains(x.SessionId)).GroupBy(x => x.SessionId)
                    .OrderByDescending(x => x.Max(item => item.OccurredAt)).ThenBy(x => x.Key, StringComparer.Ordinal).ToArray();
                Add(AnalyticsValueRow("Direct sub-agents", children.LongLength, 0));
                if (children.Length > 0)
                {
                    var agents = new StackPanel();
                    var heading = Ui.Text("Sub-agents", 13, weight: FontWeights.SemiBold); heading.Margin = new Thickness(0); agents.Children.Add(heading);
                    foreach (var child in children)
                    {
                        var name = AnalyticsEntityName(child, child.Key, true);
                        var tokens = child.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage)).TotalTokens;
                        var button = Ui.Button("", () => Forward("sessions", selectedSession: child.Key));
                        button.Content = EntityTextRow(name, tokens.ToString("N0", CultureInfo.CurrentCulture) + "  ›", 11);
                        button.Padding = new Thickness(0); button.Margin = new Thickness(0, 7, 0, 0); button.BorderThickness = new Thickness(0);
                        button.Background = Brushes.Transparent; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                        AutomationProperties.SetAutomationId(button, "usage.subagent." + child.Key);
                        AutomationProperties.SetName(button, name + ": " + tokens.ToString("N0", CultureInfo.CurrentCulture) + " tokens"); agents.Children.Add(button);
                    }
                    var note = Ui.Text("Sub-agent tokens are separate from this total.", 10, "#A6A6AA"); note.Margin = new Thickness(0, 7, 0, 0); agents.Children.Add(note); Add(agents);
                }
            }
            if (settings.Current.AttachmentMetadataEnabled && provider == "codex")
            {
                var images = EntityTextRow("Whole-session images", detail is null ? "Unavailable" : detail.Attachments.Sum(x => (long)x.Count).ToString("N0", CultureInfo.CurrentCulture));
                images.ToolTip = "Counts cover the whole session after the local-history cutoff, not just this range. Local metadata may be incomplete; attachment contents are never stored.";
                Add(images);
            }
        }
        var breakdown = new StackPanel();
        breakdown.Children.Add(AnalyticsValueRow("Input", total.InputTokens, 0));
        breakdown.Children.Add(AnalyticsValueRow("Cached input", total.CachedInputTokens, 6));
        breakdown.Children.Add(AnalyticsValueRow("Output", total.OutputTokens, 6)); Add(breakdown);
        var models = new StackPanel();
        var modelHeading = Ui.Text("Models", 13, weight: FontWeights.SemiBold); modelHeading.Margin = new Thickness(0); models.Children.Add(modelHeading);
        foreach (var model in UsageAnalytics.Group(events, "model"))
        {
            var row = EntityTextRow(model.Name, model.Tokens.ToString("N0", CultureInfo.CurrentCulture), 11); row.Margin = new Thickness(0, 7, 0, 0);
            AutomationProperties.SetAutomationId(row, "usage.entity.model." + model.Name); models.Children.Add(row);
        }
        Add(models);
    }

    private static Grid EntityTextRow(string label, string value, double fontSize = 13)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Ui.Text(label, fontSize); title.Margin = new Thickness(0, 0, 8, 0);
        title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = label; row.Children.Add(title);
        var text = Ui.Text(value, fontSize, "#A6A6AA"); text.Margin = new Thickness(0); text.TextWrapping = TextWrapping.NoWrap;
        System.Windows.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        Grid.SetColumn(text, 1); row.Children.Add(text); return row;
    }
}
