using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class UsagePane
{
    private static readonly string[] SettingsSeriesColors = ["#FF9F0A", "#30D158", "#FF375F", "#64D2FF", "#FFD60A"];
    private Brush SettingsSeriesColor(int index) => index == 0 ? (Brush)FindResource("AccentBrush")
        : Ui.Brush(SettingsSeriesColors[(index - 1) % SettingsSeriesColors.Length]);

    // Like categorical chart symbols on Mac, the range-stable series order
    // keeps each model's shape consistent even on days when its usage is zero.
    private static readonly Geometry[] SettingsModelSymbolPaths =
    [
        FrozenSettingsSymbol("M0,4 L2,0 L4,4 Z"),
        FrozenSettingsSymbol("M0,2 L2,0 L4,2 L2,4 Z"),
        FrozenSettingsSymbol("M1.5,0 L2.5,0 L2.5,1.5 L4,1.5 L4,2.5 L2.5,2.5 L2.5,4 L1.5,4 L1.5,2.5 L0,2.5 L0,1.5 L1.5,1.5 Z"),
        FrozenSettingsSymbol("M0,1 L1,0 L2,1 L3,0 L4,1 L3,2 L4,3 L3,4 L2,3 L1,4 L0,3 L1,2 Z")
    ];
    private static Geometry FrozenSettingsSymbol(string data)
    {
        var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry;
    }
    private static Shape SettingsSeriesSymbol(int index, Brush color)
    {
        var kind = index % 6;
        Shape symbol = kind switch
        {
            0 => new Ellipse(),
            1 => new Rectangle(),
            _ => new System.Windows.Shapes.Path { Data = SettingsModelSymbolPaths[kind - 2], Stretch = Stretch.Fill }
        };
        symbol.Width = symbol.Height = 4; symbol.Fill = color; symbol.IsHitTestVisible = false;
        return symbol;
    }

    private Grid SettingsUsageChart(SettingsUsageSnapshot snapshot, SettingsUsageGrouping grouping, bool line)
    {
        var series = SettingsUsageAnalytics.Series(snapshot, grouping, settings.Current.ShowCachedInput);
        var days = snapshot.Days.Select(day => (Day: day, Series: SettingsUsageAnalytics.Series(snapshot, grouping, settings.Current.ShowCachedInput, day))).ToArray();
        var maximum = line ? days.SelectMany(day => day.Series).Select(s => (double)s.Tokens).DefaultIfEmpty(0).Max()
            : days.Select(day => day.Series.Sum(s => (double)s.Tokens)).DefaultIfEmpty(0).Max();
        // A desired count is a scale hint. Round to an automatic readable step,
        // instead of promising four labels for every possible token magnitude.
        var rough = Math.Max(1, maximum) / 3; var power = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var factor = rough / power; var step = (factor <= 1 ? 1 : factor <= 2 ? 2 : factor <= 5 ? 5 : 10) * power;
        var top = Math.Max(step, Math.Ceiling(maximum / step) * step);
        var chart = new Grid { Height = line ? 160 : 190, Margin = new Thickness(0, 18, 0, 0) };
        chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) }); chart.ColumnDefinitions.Add(new ColumnDefinition());
        chart.RowDefinitions.Add(new RowDefinition()); chart.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        AutomationProperties.SetName(chart, line ? "Daily tokens by model" : "Daily token usage");
        var plot = new Canvas { ClipToBounds = true, IsHitTestVisible = false }; Grid.SetColumn(plot, 1); chart.Children.Add(plot);
        var yAxis = new Canvas { IsHitTestVisible = false }; chart.Children.Add(yAxis);
        var xAxis = new Canvas { IsHitTestVisible = false, ClipToBounds = true }; Grid.SetColumn(xAxis, 1); Grid.SetRow(xAxis, 1); chart.Children.Add(xAxis);
        var marks = new List<(Button Button, StackPanel? Stack, SettingsUsageDay Day)>();
        for (var i = 0; i < days.Length; i++)
        {
            var day = days[i].Day;
            var values = days[i].Series;
            var label = AnalyticsDateText.Format(day.Start, AnalyticsDateStyle.Day) + ": "
                + day.Usage.TotalTokens.ToString("N0", CultureInfo.CurrentCulture) + " tokens, "
                + string.Join(", ", values.Select(value => value.Title + ": " + value.Tokens.ToString("N0", CultureInfo.CurrentCulture) + " tokens"));
            var button = Ui.Button("", () => { settingsSelectedDate = day.Start; Update(); });
            button.Template = ChartBarTemplate(); Motion.SetFeedback(button, false);
            button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); button.Margin = new Thickness(0); button.Padding = new Thickness(0);
            button.MinWidth = button.MinHeight = 0; button.HorizontalAlignment = HorizontalAlignment.Left; button.VerticalAlignment = VerticalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Stretch;
            button.Cursor = System.Windows.Input.Cursors.Arrow; button.Tag = day.Start;
            var content = new Grid(); StackPanel? stack = null;
            if (!line)
            {
                stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
                for (var index = values.Count - 1; index >= 0; index--)
                    stack.Children.Add(new Border { Background = SettingsSeriesColor(index), CornerRadius = new CornerRadius(2), Tag = (double)values[index].Tokens });
                content.Children.Add(stack);
            }
            button.Content = content; button.ToolTip = label; AutomationProperties.SetName(button, label);
            AutomationProperties.SetAutomationId(button, (line ? "settings.usage.modelBucket." : "settings.usage.bucket.") + day.Start.ToUnixTimeSeconds());
            timelineButtons[AutomationProperties.GetAutomationId(button)] = button;
            Grid.SetColumn(button, 1); chart.Children.Add(button); marks.Add((button, stack, day));
        }
        void Draw()
        {
            var width = Math.Max(0, plot.ActualWidth); var height = Math.Max(0, plot.ActualHeight);
            plot.Children.Clear(); yAxis.Children.Clear(); xAxis.Children.Clear();
            var slot = width / Math.Max(1, days.Length);
            for (var tick = 0d; tick <= top + step * .001; tick += step)
            {
                var y = height * (1 - tick / top);
                var gridline = new Line { X2 = width, Y1 = y, Y2 = y, StrokeThickness = 1, Opacity = .25 };
                gridline.SetResourceReference(Shape.StrokeProperty, "DividerBrush"); plot.Children.Add(gridline);
                var count = tick >= long.MaxValue ? long.MaxValue : (long)Math.Max(0, tick);
                var text = Ui.Text(CodeRim.Core.Services.TokenFormatter.Format(count, settings.Current.NumberStyle), 11, "#A6A6AA");
                text.Margin = new Thickness(0); text.Width = 42; text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
                Canvas.SetTop(text, Math.Clamp(y - 7, 0, Math.Max(0, height - 14))); yAxis.Children.Add(text);
            }
            for (var i = 0; i < marks.Count; i++)
            {
                marks[i].Button.Width = slot; marks[i].Button.Margin = new Thickness(i * slot, 0, 0, 0);
                if (marks[i].Stack is { } stack)
                {
                    stack.Width = Math.Max(0, slot * .55);
                    foreach (var segment in stack.Children.OfType<Border>()) segment.Height = height * (double)segment.Tag / top;
                }
                if (i % (snapshot.Range == AnalyticsRange.SevenDays ? 3 : 10) == 0)
                {
                    var date = Ui.Text(AnalyticsDateText.Format(marks[i].Day.Start, AnalyticsDateStyle.AxisDay), 11, "#A6A6AA");
                    date.Margin = new Thickness(0); date.TextWrapping = TextWrapping.NoWrap;
                    date.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    date.MaxWidth = width; date.TextTrimming = TextTrimming.CharacterEllipsis;
                    var labelWidth = Math.Min(width, date.DesiredSize.Width);
                    Canvas.SetLeft(date, Math.Clamp((i + .5) * slot - labelWidth / 2, 0, Math.Max(0, width - labelWidth)));
                    xAxis.Children.Add(date);
                }
            }
            if (!line) return;
            for (var index = 0; index < series.Count; index++)
            {
                var color = SettingsSeriesColor(index); var path = new Polyline { Stroke = color, StrokeThickness = 2 };
                for (var i = 0; i < days.Length; i++)
                {
                    var x = (i + .5) * slot; var y = height * (1 - days[i].Series[index].Tokens / top);
                    path.Points.Add(new Point(x, y));
                    var symbol = SettingsSeriesSymbol(index, color);
                    Canvas.SetLeft(symbol, x - 2); Canvas.SetTop(symbol, y - 2); plot.Children.Add(symbol);
                }
                plot.Children.Add(path);
            }
        }
        plot.SizeChanged += (_, _) => Draw(); chart.Loaded += (_, _) => Draw();
        return chart;
    }
}
