using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal static partial class NotchPopover
{
    private static Grid AccountRow(string id, string? plan, Action<string?> navigate)
    {
        var row = new Grid { Height = NotchMetrics.AccountRowHeight, Margin = new Thickness(0, NotchMetrics.HeaderToBlock, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        plan = plan?.Trim();
        if (string.IsNullOrEmpty(plan)) plan = "Account";
        else if (!plan.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter))
            plan = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(plan.Replace('_', ' '));
        var label = Text(plan, NotchMetrics.CardBodyFontSize, Secondary);
        label.Margin = new Thickness(0, 0, 8, 0); label.VerticalAlignment = VerticalAlignment.Center;
        label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis; label.ToolTip = plan;
        row.Children.Add(label);
        var button = PlainButton("Switch account", () => navigate(id is "codex" or "claude" ? id + "-accounts" : id));
        var caption = Text("Switch account", NotchMetrics.CardBodyFontSize); caption.Margin = new Thickness(0);
        var action = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        action.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M6,0.5 A5.5,5.5 0 1 0 6,11.5 A5.5,5.5 0 1 0 6,0.5 M4.3,4.2 A1.7,1.7 0 1 0 7.7,4.2 A1.7,1.7 0 1 0 4.3,4.2 M2.8,9.8 Q3,7 6,7 Q9,7 9.2,9.8"),
            Stroke = Brushes.White, StrokeThickness = .8, Width = 11, Height = 11, Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center });
        action.Children.Add(caption); button.Content = action; button.Padding = new Thickness(0);
        AutomationProperties.SetAutomationId(button, "notch.switchAccount." + id);
        Grid.SetColumn(button, 1); row.Children.Add(button); return row;
    }

    private static void AddLimitGroups(StackPanel content, IReadOnlyList<LimitWindow> windows, AppSettings settings)
    {
        var now = DateTimeOffset.Now; var index = 0; var hasNamedGroup = false;
        while (index < windows.Count)
        {
            var start = index; var group = windows[index].Group;
            while (index < windows.Count && windows[index].Group == group) index++;
            if (group is not null)
            {
                hasNamedGroup = true;
                var block = new StackPanel { Margin = new Thickness(0, start == 0 ? NotchMetrics.HeaderToBlock : 28 * NotchMetrics.Unit, 0, 0) };
                var title = Text(group, NotchMetrics.CardBodyFontSize, Brushes.White, FontWeights.SemiBold);
                title.Margin = new Thickness(4 * NotchMetrics.Unit, 0, 0, 12 * NotchMetrics.Unit); block.Children.Add(title);
                var rows = new StackPanel();
                for (var position = start; position < index; position++)
                {
                    var row = LimitRow(windows[position], settings, now, 32 * NotchMetrics.Unit);
                    row.Margin = new Thickness(0, position == start ? 0 : NotchMetrics.BlockSpacing, 0, 0); rows.Children.Add(row);
                }
                var frame = new Grid(); frame.Children.Add(new Border { Child = rows, Padding = new Thickness(16 * NotchMetrics.Unit) });
                var outline = new Border { IsHitTestVisible = false, BorderBrush = Ui.Brush("#40FFFFFF"),
                    BorderThickness = new Thickness(1.5 * NotchMetrics.Unit), CornerRadius = new CornerRadius(20 * NotchMetrics.Unit) };
                AutomationProperties.SetAutomationId(outline, "notch.limit.group." + start.ToString(CultureInfo.InvariantCulture));
                frame.Children.Add(outline);
                block.Children.Add(frame); content.Children.Add(block);
            }
            else
            {
                for (var position = start; position < index; position++)
                {
                    var row = LimitRow(windows[position], settings, now, 0);
                    row.Margin = new Thickness(0, position == 0 ? NotchMetrics.HeaderToBlock : NotchMetrics.BlockSpacing, 0, 0);
                    content.Children.Add(row);
                }
            }
        }
        if (hasNamedGroup) content.Children.Add(new Border { Height = 8 * NotchMetrics.Unit });
    }

    private static StackPanel LimitRow(LimitWindow window, AppSettings settings, DateTimeOffset now, double inset)
    {
        var body = new StackPanel(); AutomationProperties.SetAutomationId(body, "notch.limit." + window.Id);
        var countOnly = window.UsedPercent is null && window.UsedCount is not null;
        var label = (Grid)Row(window.Name, countOnly ? TokenFormatter.Format(window.UsedCount!.Value, settings.NumberStyle) : Reset(window.ResetsAt, settings.ResetTime));
        label.Margin = new Thickness(0);
        foreach (var text in label.Children.OfType<TextBlock>())
        {
            text.FontSize = NotchMetrics.CardBodyFontSize; text.TextWrapping = TextWrapping.NoWrap;
            text.Margin = new Thickness(Grid.GetColumn(text) == 1 ? 20 * NotchMetrics.Unit : 0, 0, 0, 0);
        }
        body.Children.Add(label);
        if (countOnly) return body;
        if (window.UsedPercent is { } used && double.IsFinite(used))
        {
            var fill = new Border { Background = Ui.Brush(NotchGeometry.BandColor(used, settings.AccentColor)),
                CornerRadius = new CornerRadius(NotchMetrics.BarHeight / 2), HorizontalAlignment = HorizontalAlignment.Left };
            var track = new Border { Background = Ui.Brush("#2D2D2D"), CornerRadius = new CornerRadius(NotchMetrics.BarHeight / 2),
                Height = NotchMetrics.BarHeight, Margin = new Thickness(0, NotchMetrics.LabelToBar, 0, 0), Child = fill };
            track.SizeChanged += (_, _) => fill.Width = Math.Min(track.ActualWidth, Math.Max(NotchMetrics.BarHeight, track.ActualWidth * Math.Clamp(used / 100, 0, 1)));
            AutomationProperties.SetAutomationId(track, "notch.limit.bar." + window.Id); body.Children.Add(track);
        }
        var summary = Text("", NotchMetrics.CardBodyFontSize); summary.Margin = new Thickness(0); summary.TextWrapping = TextWrapping.NoWrap;
        summary.Inlines.Add(new Run(LimitFormatting.Summary(window, settings.NumberStyle)));
        if (settings.ShowUsagePace && NotchUsagePace.For(window, now) is { } pace)
            summary.Inlines.Add(new Run(" · " + pace.Summary) { Foreground = pace.IsDeficit ? Ui.Brush("#FF9500") : Secondary });
        AutomationProperties.SetAutomationId(summary, "notch.limit.summary." + window.Id);
        // Cap the unscaled line so even a long localized value cannot shrink
        // below the reference's 0.85 minimum; ellipsis handles the remainder.
        summary.MaxWidth = (NotchMetrics.CardWidth - 2 * NotchMetrics.CardPadding - inset) / .85;
        summary.TextTrimming = TextTrimming.CharacterEllipsis;
        body.Children.Add(new Viewbox { Child = summary, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, NotchMetrics.BarToUsed, 0, 0) });
        return body;
    }
}
