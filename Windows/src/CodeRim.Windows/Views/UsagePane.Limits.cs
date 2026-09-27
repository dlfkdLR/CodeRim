using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class UsagePane
{
    private readonly DispatcherTimer limitClock = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly List<Action<DateTimeOffset>> limitClockUpdates = [];
    private bool limitsAboutExpanded;
    private ProviderReading? limitClockReading;
    private string? limitClockOwnerKey;
    internal bool ShowsLimits => mode == "Limits";

    internal void RefreshLimitClock(DateTimeOffset now)
    {
        if (!IsVisible || mode != "Limits" || destination != "overview" || provider is not ("codex" or "claude")) return;
        var display = store.AccountDisplay(provider);
        // Private account changes outrank focus retention. A poll for the same
        // owner uses the ordinary deferred refresh, keeping the active control.
        if (limitClockOwnerKey != display.OwnerKey) { Update(); return; }
        if (!ReferenceEquals(limitClockReading, display.Reading)) RefreshReadings();
        foreach (var update in limitClockUpdates) update(now);
    }

    private void AccountLimits(ProviderReading? raw)
    {
        limitClockReading = raw;
        var reading = ProviderDisplayPolicy.Apply(raw, settings.Current);
        if (reading is null || reading.State is not (ReadingState.Ready or ReadingState.Stale or ReadingState.Partial) && reading.Windows.Count == 0)
        {
            if (reading?.State != ReadingState.Disabled && (store.RefreshingProviders.Contains(provider) || reading?.State == ReadingState.Loading))
            {
                var text = Ui.Text("Reading account limits…", 13, "#A6A6AA");
                text.Margin = new Thickness(0); text.VerticalAlignment = VerticalAlignment.Center;
                var loading = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                loading.Children.Add(new LimitActivityIndicator { Margin = new Thickness(0, 0, 10, 0) }); loading.Children.Add(text);
                var pending = new Grid { MinHeight = 120 }; pending.Children.Add(loading);
                AutomationProperties.SetAutomationId(pending, "usage.limits.loading"); readings.Children.Add(pending);
            }
            else LimitsUnavailable(reading?.Message ?? "Account limits unavailable");
            return;
        }
        var quotas = reading.Windows.Where(window => window.Id != ProviderDisplayPolicy.ResetCreditsId).ToArray();
        var windows = provider == "codex" ? AccountLimitPresentation.VisibleWindows(quotas, settings.Current.AdditionalLimitsEnabled) : quotas;
        if (windows.Count == 0)
            LimitsUnavailable(raw?.Windows.Any(window => window.Id != ProviderDisplayPolicy.ResetCreditsId) == true
                ? "No Codex limit window was reported. Enable additional limits to view the other windows."
                : "No account limit windows were returned.");
        foreach (var window in windows) AddLimitBlock(AccountLimitCard(window, reading));
        if (provider == "codex" && reading.Windows.FirstOrDefault(window => window.Id == ProviderDisplayPolicy.ResetCreditsId) is { } credits)
            readings.Children.Add(ResetCreditsCard(credits));

        var help = Ui.Text(provider == "codex"
            ? "Reported by Codex; read-only. Reset credits are never consumed here. Pace assumes even use; run-out times are estimates, not guarantees."
            : "Reported by Claude Code; read-only. Limits update after a Claude response. Pace assumes even use; run-out times are estimates, not guarantees.", 11, "#A6A6AA");
        help.Margin = new Thickness(0, 4, 0, 0);
        var about = new Expander { Header = "About these limits", Content = help, IsExpanded = limitsAboutExpanded,
            Style = (Style)FindResource("UsageLimitsDisclosure") };
        AutomationProperties.SetAutomationId(about, "usage.limits.about");
        AutomationProperties.SetName(about, "About these limits");
        about.Expanded += (_, _) => limitsAboutExpanded = true; about.Collapsed += (_, _) => limitsAboutExpanded = false;
        AddLimitBlock(about);

        var freshness = Ui.Text("", 11, "#A6A6AA"); freshness.Margin = new Thickness(0);
        AutomationProperties.SetAutomationId(freshness, "usage.limits.freshness");
        var freshnessIcon = LimitIcon("M6,1 A5,5 0 1 1 5.99,1 M6,3 V6 L8,7", 12);
        var freshnessRow = new DockPanel(); DockPanel.SetDock(freshnessIcon, Dock.Left); freshnessRow.Children.Add(freshnessIcon); freshnessRow.Children.Add(freshness); AddLimitBlock(freshnessRow);
        void UpdateFreshness(DateTimeOffset now)
        {
            var stale = LimitsAreStale(reading, now);
            freshness.Text = reading.UpdatedAt is { } fetched ? AccountLimitPresentation.Freshness(fetched, now, stale) : "Update time unavailable";
            freshnessIcon.Data = Geometry.Parse(stale ? "M1,4 Q6,0 11,4 M3,7 Q6,4 9,7 M5,10 L6,11 L7,10 M1,1 L11,11" : "M6,1 A5,5 0 1 1 5.99,1 M6,3 V6 L8,7");
        }
        limitClockUpdates.Add(UpdateFreshness); UpdateFreshness(DateTimeOffset.Now);
    }

    private static bool LimitsAreStale(ProviderReading reading, DateTimeOffset now)
        => reading.State != ReadingState.Ready || reading.IsStale(now);

    private void AddLimitBlock(FrameworkElement block)
    {
        block.Margin = new Thickness(0, readings.Children.Count == 0 ? 0 : 16, 0, 0);
        readings.Children.Add(block);
    }

    private void LimitsUnavailable(string message)
    {
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var icon = LimitIcon("M2,11 A5,5 0 1 1 12,11 M7,10 L4,7 M3,5 L4,6 M7,3 V5 M11,5 L10,6", 32);
        icon.HorizontalAlignment = HorizontalAlignment.Center; icon.Margin = new Thickness(0, 0, 0, 8); content.Children.Add(icon);
        var title = Ui.Text("Limits Unavailable", 16, weight: FontWeights.SemiBold); title.TextAlignment = TextAlignment.Center;
        var detail = Ui.Text(message, 12, "#A6A6AA"); detail.TextAlignment = TextAlignment.Center;
        content.Children.Add(title); content.Children.Add(detail);
        var empty = new Grid { MinHeight = 160 }; empty.Children.Add(content);
        AutomationProperties.SetAutomationId(empty, "usage.limits.unavailable"); AddLimitBlock(empty);
    }

    private Border AccountLimitCard(LimitWindow window, ProviderReading reading)
    {
        var name = AccountLimitPresentation.Name(window, provider);
        var label = AccountLimitPresentation.WindowLabel(window.DurationMinutes);
        var content = new StackPanel { Margin = new Thickness(12) };
        var header = new DockPanel { LastChildFill = true };
        var amount = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var remaining = window.RemainingPercent is { } value && double.IsFinite(value) ? value : (double?)null;
        var number = Ui.Text(remaining is { } known ? Math.Round(known, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.CurrentCulture) : "—", 13, weight: FontWeights.SemiBold);
        number.Margin = new Thickness(0); System.Windows.Documents.Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        var suffix = Ui.Text("% left", 11, "#A6A6AA"); suffix.Margin = new Thickness(4, 0, 0, 0); suffix.VerticalAlignment = VerticalAlignment.Center;
        amount.Children.Add(number); amount.Children.Add(suffix); DockPanel.SetDock(amount, Dock.Right); header.Children.Add(amount);
        var identity = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var title = Ui.Text(name, 13, weight: FontWeights.SemiBold); title.Margin = new Thickness(0, 0, 0, 2);
        var periodLabel = Ui.Text(label, 11, "#A6A6AA"); periodLabel.Margin = new Thickness(0);
        identity.Children.Add(title); identity.Children.Add(periodLabel); header.Children.Add(identity); content.Children.Add(header);
        var progress = new ProgressBar { Value = remaining ?? 0, Style = (Style)FindResource("ProviderUsageProgress"), Margin = new Thickness(0, 8, 0, 0) };
        progress.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        AutomationProperties.SetName(progress, name + ", " + label + ", remaining");
        AutomationProperties.SetAutomationId(progress, "usage.limit.remaining." + window.Id);
        content.Children.Add(progress);
        TextBlock? resetText = null;
        if (window.ResetsAt is { } reset)
        {
            resetText = Ui.Text("", 11, "#A6A6AA"); resetText.Margin = new Thickness(0, 8, 0, 0);
            resetText.ToolTip = "Resets " + reset.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
            AutomationProperties.SetAutomationId(resetText, "usage.limit.reset." + window.Id); content.Children.Add(resetText);
        }
        var paceText = Ui.Text("", 11, "#A6A6AA"); paceText.Margin = new Thickness(0, 8, 0, 0);
        paceText.ToolTip = "Pace compares current use with even use across the reported limit window. Run-out time uses the current window average.";
        AutomationProperties.SetAutomationId(paceText, "usage.limit.pace." + window.Id);
        paceText.Margin = new Thickness(0);
        var paceIcon = LimitIcon("M1,10 A5,5 0 1 1 11,10 M6,8 L8,5", 12);
        var paceRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(paceIcon, Dock.Left);
        paceRow.Children.Add(paceIcon); paceRow.Children.Add(paceText); content.Children.Add(paceRow);
        void UpdateTimes(DateTimeOffset now)
        {
            if (resetText is not null) resetText.Text = ResetCopy.Text(window.ResetsAt, "Relative", now);
            var pace = !LimitsAreStale(reading, now) ? AccountLimitPresentation.Pace(window, now) : null;
            paceRow.Visibility = pace is null ? Visibility.Collapsed : Visibility.Visible;
            paceText.SetResourceReference(TextBlock.ForegroundProperty, pace?.State == LimitPaceState.Ahead ? "UsageCritical" : "SecondaryText");
            paceIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, pace?.State == LimitPaceState.Ahead ? "UsageCritical" : "SecondaryText");
            paceIcon.Data = Geometry.Parse(pace?.State == LimitPaceState.Ahead ? "M6,1 L11,10 H1 Z M6,4 V6.5 M6,8 V8.5" : "M1,10 A5,5 0 1 1 11,10 M6,8 L8,5");
            paceText.Text = pace is null ? "" : pace.Summary + (pace.ProjectedExhaustion is { } runOut
                ? " · estimated run-out " + RelativeRunOut(runOut, now) : "");
        }
        limitClockUpdates.Add(UpdateTimes); UpdateTimes(DateTimeOffset.Now);
        var card = new Border { Child = content, CornerRadius = new CornerRadius(10) };
        card.SetResourceReference(Border.BackgroundProperty, "LimitCardBackground");
        AutomationProperties.SetAutomationId(card, "usage.limit." + window.Id); return card;
    }

    private static System.Windows.Shapes.Path LimitIcon(string geometry, double size)
    {
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = size, Height = size,
            Stretch = Stretch.Uniform, StrokeThickness = 1, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText"); return icon;
    }

    private static string RelativeRunOut(DateTimeOffset date, DateTimeOffset now)
    {
        var minutes = Math.Max(0, (int)Math.Round((date - now).TotalMinutes, MidpointRounding.AwayFromZero));
        return minutes >= 1440 ? (minutes / 1440).ToString(CultureInfo.CurrentCulture) + "d " + (minutes / 60 % 24).ToString(CultureInfo.CurrentCulture) + "h"
            : minutes >= 60 ? (minutes / 60).ToString(CultureInfo.CurrentCulture) + "h " + (minutes % 60).ToString(CultureInfo.CurrentCulture) + "m"
            : minutes.ToString(CultureInfo.CurrentCulture) + " min";
    }
}
