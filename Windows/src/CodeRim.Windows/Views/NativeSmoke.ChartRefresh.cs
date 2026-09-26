using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private sealed class AnalyticsBoundaryClock : TimeProvider
    {
        private DateTimeOffset first;
        private DateTimeOffset later;
        internal int Reads { get; private set; }
        internal void Reset(DateTimeOffset value, DateTimeOffset next) { first = value; later = next; Reads = 0; }
        public override DateTimeOffset GetUtcNow() => (++Reads == 1 ? first : later).ToUniversalTime();
    }

    private static async Task ChartRefreshRegression(DashboardStore store, AppSettingsStore settings, string directory)
    {
        var previousEvents = store.Events.GetValueOrDefault("codex");
        var previousUsage = store.Usage.GetValueOrDefault("codex");
        var previousCost = settings.Current.CostEstimatesEnabled;
        Window? window = null; Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            settings.Save(settings.Current with { CostEstimatesEnabled = true });
            var day = new DateTimeOffset(new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Local));
            // Construct each local date anew: DateTimeOffset.AddDays preserves
            // September's offset even after an October daylight-saving change.
            DateTimeOffset LocalDate(int days, int hour = 0) => new(DateTime.SpecifyKind(day.Date.AddDays(days).AddHours(hour), DateTimeKind.Local));
            var before = day.AddTicks(-1); var after = day.AddSeconds(1);
            var clock = new AnalyticsBoundaryClock(); clock.Reset(before, after);
            void SetEvents(params UsageEvent[] events)
            {
                store.Events["codex"] = events;
                store.Usage["codex"] = UsageScanner.Aggregate(events, after, settings.Current.WeekStart, false);
            }
            var old = new UsageEvent("before-midnight", before, new(111, 0, 0, 0), "gpt-5.6-sol");
            var current = new UsageEvent("after-midnight", day, new(222, 0, 0, 0), "gpt-5.6-sol");
            SetEvents(old, current);
            var pane = new UsagePane(store, settings, "codex", _ => { }, clock) { Width = 632 };
            window = new Window { Content = pane, Width = 680, Height = 760, Title = "Chart snapshot boundary fixture" };
            window.Show(); await Idle();
            Descendants<Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.destination.activity").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Descendants<RadioButton>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.range.today").IsChecked = true;
            Button[] Bars() => Descendants<Button>(pane).Where(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.tokens.", StringComparison.Ordinal)).ToArray();
            void Axis(DateTimeOffset[] dates, double[] positions, double height)
            {
                var frame = (Grid)Bars()[0].Parent;
                var labels = Descendants<TextBlock>(Descendants<Grid>(frame).Single(x => AutomationProperties.GetAutomationId(x) == "usage.chart.axis.tokens")).ToArray();
                var lines = Descendants<System.Windows.Shapes.Line>(frame).ToArray();
                Require(lines.Length == dates.Length && labels.Length == dates.Length && frame.ActualHeight == height,
                    "Axis fixture is missing expected tick lines, labels or its chart height");
                for (var i = 0; i < dates.Length; i++)
                {
                    Require(Equals(lines[i].Tag, dates[i]) && Equals(labels[i].Tag, dates[i])
                        && Math.Abs(lines[i].X1 - positions[i]) < .1 && lines[i].X1 == lines[i].X2
                        && Math.Abs(Canvas.GetLeft(labels[i]) - positions[i] - 4) < .1 && lines[i].Y2 == height,
                        "Axis tick, label and date coordinates disagree with the native reference");
                    Require(!lines[i].IsHitTestVisible && !labels[i].IsHitTestVisible && !lines[i].Focusable && !labels[i].Focusable,
                        "Decorative chart axes intercept pointer or keyboard input");
                    Require(lines[i].StrokeDashArray.SequenceEqual(new double[] { 3, 3 }), "Axis lost its reference dashed grid");
                }
            }
            void Total(long expected, string id)
            {
                var panel = Descendants<StackPanel>(pane).First(x => AutomationProperties.GetAutomationId(x) == id);
                Require(Descendants<TextBlock>(panel).Any(x => x.Text == expected.ToString("N0", CultureInfo.CurrentCulture)), "Wrong chart snapshot value in " + id);
            }
            clock.Reset(before, after); pane.Update(); await Idle();
            Require(clock.Reads == 1, "An analytics render read more than one snapshot time");
            Total(111, "analytics.summary.tokens");
            var focused = Bars()[^1];
            Require(AutomationProperties.GetName(focused).Contains("111 tokens", StringComparison.Ordinal), "Chart used the next day while its summary still used the prior day");
            focused.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Require(focused.Focus(), "Cannot establish focused chart refresh fixture");
            clock.Reset(after, after); pane.RefreshReadings(); await Idle();
            Require(clock.Reads == 1 && Bars().Length == 1 && Bars()[0].IsKeyboardFocused, "Rollover refresh froze data or discarded chart keyboard focus");
            Total(222, "analytics.summary.tokens"); Total(222, "usage.bucket-details");
            var rolloverTarget = Bars()[0];
            SetEvents(old, current, new("same-quality-update", day.AddMilliseconds(1), new(111, 0, 0, 0), "gpt-5.6-sol"));
            clock.Reset(after, after); pane.RefreshReadings(); await Idle();
            Require(clock.Reads == 1 && !ReferenceEquals(rolloverTarget, Bars()[0]) && Bars()[0].IsKeyboardFocused,
                "Exact-to-exact focused refresh did not replace values and preserve its target");
            Total(333, "analytics.summary.tokens"); Total(333, "usage.bucket-details");
            Capture(pane, System.IO.Path.Combine(directory, "windows-chart-focused-refresh.png"));

            // Use the native Mac probe's 600-point / 02:30 deterministic geometry.
            var through = day.AddMinutes(150);
            store.Events["codex"] = [new("a", day, new(10, 0, 0, 0), "gpt-5.6-sol"),
                new("b", day.AddHours(1), new(20, 0, 0, 0), "gpt-5.6-sol"), new("c", day.AddHours(2), new(30, 0, 0, 0), "gpt-5.6-sol")];
            clock.Reset(through, through); pane.Update(); await Idle();
            var bars = Bars(); var chart = (Grid)bars[0].Parent;
            Require(Math.Abs(chart.ActualWidth - 600) < .1 && bars.Length == 3, "Reference geometry fixture is not the native probe's size/range");
            double[] centers = [0, 240, 480]; double[] heights = [62d / 3, 124d / 3, 62];
            for (var i = 0; i < bars.Length; i++)
            {
                var fill = Descendants<Border>((Grid)bars[i].Content).Single();
                var bounds = fill.TransformToAncestor(chart).TransformBounds(new Rect(fill.RenderSize));
                Require(Math.Abs(bounds.Left + bounds.Width / 2 - centers[i]) < .1 && Math.Abs(bounds.Width - 8) < .1
                    && Math.Abs(bounds.Height - heights[i]) < .1 && Math.Abs(bounds.Bottom - 62) < .1,
                    "Windows chart differs from the measured native Date-scale mark geometry");
            }
            Require(Math.Abs(UsagePane.BarHeight(1, 1000000, 62) - .000062) < .0000001, "A very small value was inflated to a two-pixel minimum");
            Axis([day, day.AddHours(1), day.AddHours(2)], centers, 80);
            Capture(pane, System.IO.Path.Combine(directory, "windows-chart-native-geometry.png"));
            bars[^1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(bars[^1].Focus(), "Cannot focus the final partial-hour fixture");
            var rolledBack = day.AddMinutes(90); clock.Reset(rolledBack, rolledBack); pane.RefreshReadings(); await Idle();
            Require(Bars().Length == 2 && Bars()[^1].IsKeyboardFocused, "Clock rollback restored focus to the first rather than nearest available interval");
            Total(20, "usage.bucket-details");
            clock.Reset(day, day); pane.Update(); await Idle();
            var midnight = Bars().Single(); var midnightChart = (Grid)midnight.Parent;
            var midnightFill = Descendants<Border>((Grid)midnight.Content).Single();
            var midnightBounds = midnightFill.TransformToAncestor(midnightChart).TransformBounds(new Rect(midnightFill.RenderSize));
            Require(Math.Abs(midnightBounds.Left + midnightBounds.Width / 2 - 300) < .1 && Math.Abs(midnightBounds.Height - 80) < .1
                && !Descendants<Grid>(midnightChart).Any(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.chart.axis.", StringComparison.Ordinal)),
                "Zero-length midnight domain is not centered with the reference's axis-free full height");
            Capture(pane, System.IO.Path.Combine(directory, "windows-chart-midnight.png"));
            var halfHour = day.AddMinutes(30); clock.Reset(halfHour, halfHour); pane.Update(); await Idle();
            Require(Bars().Length == 1, "Half-hour axis fixture has unexpected hourly buckets");
            Axis([day, day.AddMinutes(15), halfHour], [0, 300, 600], 80);
            Capture(pane, System.IO.Path.Combine(directory, "windows-chart-half-hour-axis.png"));
            settings.Save(settings.Current with { CostEstimatesEnabled = false }); pane.Update(); await Idle();
            Axis([day, day.AddMinutes(15), halfHour], [0, 300, 600], 112);
            Capture(pane, System.IO.Path.Combine(directory, "windows-chart-token-only-axis.png"));
            settings.Save(settings.Current with { CostEstimatesEnabled = true });
            var weekThrough = LocalDate(6, 12); clock.Reset(weekThrough, weekThrough);
            Descendants<RadioButton>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.range.7d").IsChecked = true; await Idle();
            DateTimeOffset[] weekDates = [day, LocalDate(2), LocalDate(4), LocalDate(6)];
            Axis(weekDates, weekDates.Select(date => 600 * (date - day).TotalSeconds / (weekThrough - day).TotalSeconds).ToArray(), 80);
            Capture(pane, System.IO.Path.Combine(directory, "windows-chart-week-axis.png"));
            var monthThrough = LocalDate(29, 12); clock.Reset(monthThrough, monthThrough);
            store.Events["codex"] = Enumerable.Range(0, 30).Select(i => new UsageEvent("month-" + i,
                LocalDate(i), new(10, 0, 0, 0), "gpt-5.6-sol")).ToArray();
            Descendants<RadioButton>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.range.30d").IsChecked = true;
            pane.Width = 360; await Idle(); pane.UpdateLayout();
            var monthBars = Bars(); var monthChart = (Grid)monthBars[0].Parent;
            Require(monthBars.Length == 30 && Math.Abs(monthChart.ActualWidth - 328) < .1,
                "Narrow hit-test fixture did not establish thirty marks in a328point plot");
            var first = ((int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek - (int)day.DayOfWeek + 7) % 7;
            var days = Enumerable.Range(0, 5).Select(i => first + i * 7).Where(offset => offset <= 29).ToArray();
            var monthDates = days.Select(offset => LocalDate(offset)).ToArray();
            Axis(monthDates, monthDates.Select(date => 328 * (date - day).TotalSeconds / (monthThrough - day).TotalSeconds).ToArray(), 80);
            for (var i = 0; i < monthBars.Length; i++)
            {
                var fill = Descendants<Border>((Grid)monthBars[i].Content).Single();
                var bounds = fill.TransformToAncestor(monthChart).TransformBounds(new Rect(fill.RenderSize));
                var point = new Point(Math.Clamp(bounds.Right - .1, 0, monthChart.ActualWidth - .1), 30);
                var hit = monthChart.InputHitTest(point) as DependencyObject;
                while (hit is not null && hit is not Button) hit = VisualTreeHelper.GetParent(hit);
                Require(ReferenceEquals(hit, monthBars[i]), "A narrow chart bar's edge routes input to a neighboring interval");
            }
            Capture(pane, System.IO.Path.Combine(directory, "windows-chart-narrow-hit-regions.png"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "windows-chart-refresh.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                completed = true, checks = new List<string> { "One snapshot time across midnight", "Focused rollover refresh and nearest selection", "Exact-to-exact focused value refresh", "Clock rollback restores nearest focus",
                    "600-point Date-scale mark centers and 8-point widths", "Uninflated small values and shared baseline", "Zero-length midnight domain", "Narrow 30D mark-edge native hit testing",
                    "Hourly date ticks and label coordinates", "Half-hour intermediate ticks independent of hourly buckets", "80 and 112 point dashed grids without input interception", "Two-day weekly axis", "Calendar week-aligned month axis at328points" },
                hoverSelection = "not verified", automaticAxisParity = "Measured tick dates and geometry verified; universal font, clipping and live interaction parity not established"
            }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { window?.Close(); } catch (Exception error) { cleanup.Add(error); }
            try { if (previousEvents is null) store.Events.Remove("codex"); else store.Events["codex"] = previousEvents; } catch (Exception error) { cleanup.Add(error); }
            try { if (previousUsage is null) store.Usage.Remove("codex"); else store.Usage["codex"] = previousUsage; } catch (Exception error) { cleanup.Add(error); }
            try { settings.Save(settings.Current with { CostEstimatesEnabled = previousCost }); } catch (Exception error) { cleanup.Add(error); }
        }
        if (failure is not null && cleanup.Count > 0) throw new AggregateException("Chart fixture and cleanup failed", cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanup.Count > 0) throw new AggregateException("Chart fixture cleanup failed", cleanup);
    }
}
