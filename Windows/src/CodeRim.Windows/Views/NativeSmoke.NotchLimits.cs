using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task NotchLimitsRegression(DashboardStore store, AppSettingsStore settings, string directory)
    {
        var previous = store.Readings.GetValueOrDefault("codex"); Window? host = null;
        Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            var now = DateTimeOffset.Now;
            store.Readings["codex"] = new("codex", ReadingState.Ready, [
                new("zero", "Weekly", 0, now.AddMinutes(150), 300, Group: "Plan"),
                new("over", "Additional quota", 150, now.AddMinutes(150), 300, Group: "Plan"),
                new("count", "Requests", UsedCount: 12345),
                new("remaining", "Remaining requests", RemainingCount: 3, Group: "Plan")], now, Plan: "team_plan");
            string? destination = null;
            var preferences = settings.Current with { AccountLimitsEnabled = true, AdditionalLimitsEnabled = true,
                NumberStyle = TokenNumberStyle.Detailed, ShowUsagePace = true, Edge = NotchEdge.Right };
            var card = NotchPopover.Create("codex", store, preferences, route => destination = route, 680);
            card.HorizontalAlignment = HorizontalAlignment.Left; card.VerticalAlignment = VerticalAlignment.Top;
            host = new Window { Width = 320, Height = 680, ShowInTaskbar = false, Content = card }; host.Show(); await Idle();
            TextBlock TextAt(string id) => Descendants<TextBlock>(card).Single(x => AutomationProperties.GetAutomationId(x) == id);
            Require(TextAt("notch.title.codex").Text == "Codex Usage", "Provider tooltip title differs from the reference.");
            Require(Descendants<TextBlock>(card).Any(x => x.Text == "Team Plan")
                && !Descendants<TextBlock>(card).Any(x => RenderedText(x).Contains("preview@example.invalid", StringComparison.Ordinal)),
                "Notch plan line lost its formatted plan or retained the non-reference email row.");
            var switcher = Descendants<Button>(card).Single(x => AutomationProperties.GetAutomationId(x) == "notch.switchAccount.codex");
            switcher.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Require(destination == "codex-accounts", "Styled account switch lost its navigation action.");
            var groups = Descendants<Border>(card).Where(x => AutomationProperties.GetAutomationId(x).StartsWith("notch.limit.group.", StringComparison.Ordinal)).ToArray();
            Require(groups.Length == 2 && groups.All(x => x.ActualWidth > 0), "Repeated nonadjacent groups were merged or lost their outline.");
            var zero = Descendants<Border>(card).Single(x => AutomationProperties.GetAutomationId(x) == "notch.limit.bar.zero");
            var over = Descendants<Border>(card).Single(x => AutomationProperties.GetAutomationId(x) == "notch.limit.bar.over");
            Require(Math.Abs(((Border)zero.Child).ActualWidth - NotchMetrics.BarHeight) < 1
                && Math.Abs(((Border)over.Child).ActualWidth - over.ActualWidth) < 1, "Zero/over-limit bar shape differs from the reference.");
            var count = Descendants<StackPanel>(card).Single(x => AutomationProperties.GetAutomationId(x) == "notch.limit.count");
            Require(count.Children.Count == 1 && Descendants<TextBlock>(count).Any(x => x.Text == TokenFormatter.Format(12345, TokenNumberStyle.Detailed)),
                "Count-only usage no longer fits on its single table row.");
            Require(!Descendants<Border>(card).Any(x => AutomationProperties.GetAutomationId(x) == "notch.limit.bar.remaining"),
                "A denominator-free quota received a fabricated progress bar.");
            var summary = TextAt("notch.limit.summary.over"); var summaryText = RenderedText(summary);
            Require(summaryText == "150% Used · 0% left · 50% deficit"
                && summary.Inlines.OfType<Run>().Last().Foreground is SolidColorBrush brush && brush.Color == Color.FromRgb(255, 149, 0),
                "Pace is not inline, lost over-limit data, or omitted the deficit color.");
            var bounds = summary.TransformToAncestor(card).TransformBounds(new Rect(summary.RenderSize));
            Require(bounds.Right <= card.ActualWidth + 1 && bounds.Width / summary.ActualWidth >= .849,
                "Quota summary clips or shrinks below the reference minimum.");
            Capture(card, Path.Combine(directory, "windows-notch-limit-groups.png"));
            host.Content = NotchPopover.Create("codex", store, preferences with { ShowUsagePace = false }, _ => { }); await Idle();
            Require(!Descendants<TextBlock>(host).Any(x => RenderedText(x).Contains("% deficit", StringComparison.Ordinal)
                || RenderedText(x).Contains("% reserved", StringComparison.Ordinal)), "Turning off pace retained its inline metric.");
            var longTitleCard = NotchPopover.Create("moonshot", store, preferences, _ => { });
            longTitleCard.HorizontalAlignment = HorizontalAlignment.Left; longTitleCard.VerticalAlignment = VerticalAlignment.Top;
            host.Content = longTitleCard; await Idle();
            var longTitle = Descendants<TextBlock>(longTitleCard).Single(x => AutomationProperties.GetAutomationId(x) == "notch.title.moonshot");
            var titleBounds = longTitle.TransformToAncestor(longTitleCard).TransformBounds(new Rect(longTitle.RenderSize));
            Require(longTitle.Text == "Moonshot / Kimi Open Platform Usage" && titleBounds.Width / longTitle.ActualWidth is >= .849 and < 1
                && titleBounds.Right <= NotchMetrics.CardWidth - NotchMetrics.CardPadding + 1,
                "Long provider title skips the reference's minimum-scale behavior or clips its card.");
            Capture(longTitleCard, Path.Combine(directory, "windows-notch-long-title.png"));
            File.WriteAllText(Path.Combine(directory, "windows-notch-limit-groups.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Reference title, plan row and account-switch route", "Consecutive groups retain separate outlined sections",
                    "Zero and exceeded quota bars preserve reported values", "Count-only row and unknown-denominator handling",
                    "Inline pace, deficit color, minimum scale and disabled preference", "Long catalogue title scales before ellipsis" } }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            if (previous is null) store.Readings.Remove("codex"); else store.Readings["codex"] = previous;
            try { host?.Close(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
        }
        if (failure is not null && cleanup.Count > 0) throw new AggregateException("Notch limits fixture and cleanup failed.", new[] { failure }.Concat(cleanup));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanup.Count > 0) throw new AggregateException("Notch limits fixture cleanup failed.", cleanup);
    }
}
