using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

/// <summary>One black silhouette; no native ToolTip border, padding or focus chrome.</summary>
internal sealed class SessionExpansionState
{
    internal bool Expanded { get; set; }
}

internal static class NotchPopover
{
    internal static FrameworkElement Create(string id, DashboardStore store, AppSettings settings, Action<string?> navigate, double? availableHeight = null, SessionExpansionState? expansion = null)
    {
        var content = new StackPanel { Margin = new Thickness(NotchMetrics.CardPadding) };
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        var mark = new ProviderMark { ProviderId = id, Width = 18, Height = 18, Margin = new Thickness(0, 0, 7, 0) };
        header.Children.Add(mark);
        header.Children.Add(Text(ProviderCatalog.Find(id)?.Name ?? id, 13.7, Brushes.White, FontWeights.SemiBold));
        content.Children.Add(header);
        var accountDisplay = store.AccountDisplay(id);
        var reading = ProviderDisplayPolicy.Apply(accountDisplay.Reading?.Evaluated(DateTimeOffset.Now), settings);
        var account = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        var switcher = PlainButton("Switch account", () => navigate(id is "codex" or "claude" ? id + "-accounts" : id));
        switcher.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(switcher, Dock.Right);
        account.Children.Add(switcher);
        var identity = accountDisplay.Label;
        account.Children.Add(Text(accountDisplay.Plan ?? "Account", 10.5, Secondary));
        content.Children.Add(account);
        if (identity is not null)
        {
            var accountLabel = Text(identity, 10.5, Secondary); accountLabel.TextWrapping = TextWrapping.NoWrap;
            accountLabel.TextTrimming = TextTrimming.CharacterEllipsis; accountLabel.ToolTip = "CLI login file · " + identity;
            content.Children.Add(accountLabel);
        }
        if (store.Usage.TryGetValue(id, out var local))
        {
            var quality = local.Quality == DataQuality.Exact ? "" : " (partial)";
            content.Children.Add(Row("Today · This PC", TokenFormatter.Format(local.Today.TotalTokens, settings.NumberStyle) + " tokens" + quality));
        }
        string? group = null;
        foreach (var window in reading?.Windows ?? [])
        {
            if (window.Group is { Length: > 0 } nextGroup && nextGroup != group)
                content.Children.Add(Text(nextGroup, 10.5, Secondary, FontWeights.SemiBold));
            group = window.Group;
            content.Children.Add(Row(window.Name, Reset(window.ResetsAt, settings.ResetTime)));
            string? pace = null;
            if (window.UsedPercent is { } percent && double.IsFinite(percent))
            {
                content.Children.Add(UsageBar(percent, settings.AccentColor));
                if (settings.ShowUsagePace && window.DurationMinutes > 0 && window.ResetsAt is { } reset)
                {
                    var elapsed = Math.Clamp(1 - (reset - DateTimeOffset.Now).TotalMinutes / window.DurationMinutes, 0, 1) * 100;
                    pace = percent > elapsed + 5 ? "Above even pace" : "Within even pace";
                }
            }
            content.Children.Add(Text(LimitFormatting.Summary(window, settings.NumberStyle), 10.5, Secondary));
            if (pace is not null) content.Children.Add(Text(pace, 10.5, Secondary));
        }
        if (reading is null) content.Children.Add(Text("Waiting for a reading…", 10.5, Secondary));
        else if (reading.State != ReadingState.Ready)
        {
            content.Children.Add(Text(reading.Message ?? reading.State.ToString(), 10.5, Ui.Brush("#F2FF00")));
            if (reading.State == ReadingState.NeedsAuth) content.Children.Add(PlainButton("Connect " + (ProviderCatalog.Find(id)?.Name ?? id), () => navigate(id)));
        }
        var sessions = store.Sessions.Where(x => x.Provider == id && (settings.ShowUnknownSessions || x.State != "unavailable")).ToArray();
        var groups = SessionPresentation.Groups(sessions);
        var tokenTotals = settings.ShowSessionTokens ? store.TokensForSessions(id) : null;
        if (groups.Count > 0)
        {
            expansion ??= new SessionExpansionState();
            content.Measure(new Size(NotchMetrics.CardWidth, double.PositiveInfinity));
            var remaining = (availableHeight ?? SystemParameters.WorkArea.Height) - 40 - content.DesiredSize.Height - 2 * NotchMetrics.CardPadding - 42;
            var cap = Math.Max(0, (int)Math.Floor(remaining / (26 + 30 * NotchMetrics.Unit + 4)));
            var list = new StackPanel(); content.Children.Add(list);
            void RenderSessions(bool focusDisclosure = false)
            {
                list.Children.Clear();
                list.Children.Add(new Border { Height = 1, Background = Ui.Brush("#303030"), Margin = new Thickness(0, 8, 0, 8) });
                var shown = expansion.Expanded ? groups : groups.Take(cap).ToArray();
                foreach (var group in shown)
                {
                    foreach (var row in new[] { group.Parent }.Concat(group.Children))
                    {
                        var session = row.Session;
                        var state = row.ContextOnly ? "" : session.State switch { "busy" => "working", "waiting" => "waiting", "unavailable" => "unknown", _ => "idle" };
                        var title = session.CodexThreadId is not null && session.RemoteHostId is null ? session.Detail ?? session.Name : session.Name;
                        if (row.Depth > 0) title = "↳ " + title;
                        var detail = session.CodexThreadId is not null && session.RemoteHostId is null ? session.Name : session.Detail ?? "";
                        var openLabel = row.Depth > 0 ? "Open sub-agent " + title.TrimStart('↳', ' ') + " of " + (session.ParentThreadTitle ?? group.Parent.Session.Detail ?? group.Parent.Session.Name) + " in Codex"
                            : "Open " + title;
                        var open = PlainButton(openLabel, () => { if (!SessionFocus.Activate(session)) navigate("sessions:" + id); });
                        var stateColor = session.State == "busy" ? Ui.Brush(settings.AccentColor) : session.State == "waiting" ? Ui.Brush("#F2FF00") : Secondary;
                        var sessionContent = new StackPanel { Margin = new Thickness(14 * NotchMetrics.Unit * Math.Min(row.Depth, 2), 20 * NotchMetrics.Unit, 0, 0) };
                        var firstLine = (Grid)Row(title, state, stateColor); ConfigureSessionLine(firstLine);
                        if (!row.ContextOnly && session.State != "unavailable")
                        {
                            var statusText = (TextBlock)firstLine.Children[1]; firstLine.Children.Remove(statusText);
                            var indicator = new StackPanel { Orientation = Orientation.Horizontal };
                            indicator.Children.Add(new SessionStatusRing(session.State, stateColor) { Margin = new Thickness(8, 0, NotchMetrics.StatusDotGap, 0), VerticalAlignment = VerticalAlignment.Center });
                            statusText.Margin = new Thickness(0); indicator.Children.Add(statusText); Grid.SetColumn(indicator, 1); firstLine.Children.Add(indicator);
                        }
                        sessionContent.Children.Add(firstLine);
                        System.Windows.Automation.AutomationProperties.SetAutomationId(open, "notch.session." + session.Id);
                        var duration = row.ContextOnly ? null : SessionPresentation.Duration(session, settings.ShowSessionDuration, DateTimeOffset.Now);
                        var tokens = row.Depth == 0 && tokenTotals?.TryGetValue(session.Id, out var total) == true ? TokenFormatter.Format(total, TokenNumberStyle.Compact) + " tokens" : null;
                        var metrics = string.Join(" · ", new[] { duration, tokens }.Where(x => x is not null));
                        System.Windows.Automation.AutomationProperties.SetItemStatus(open, row.ContextOnly ? "Parent chat" : state);
                        var secondLine = (Grid)Row(detail, metrics); ConfigureSessionLine(secondLine);
                        secondLine.Margin = new Thickness(0, 10 * NotchMetrics.Unit, 0, 0); ((TextBlock)secondLine.Children[0]).Foreground = Secondary;
                        sessionContent.Children.Add(secondLine); open.Content = sessionContent; open.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                        list.Children.Add(open);
                        open.ToolTip = session.RemoteHostId is null ? openLabel : "Remote task · live status unavailable. Open in Codex.";
                    }
                }
                var hidden = groups.Count - shown.Count;
                if (hidden > 0 || expansion.Expanded)
                {
                    var disclosure = PlainButton(expansion.Expanded ? "Show less  ⌃" : "and " + hidden + " more  ⌄", () =>
                    { expansion.Expanded = !expansion.Expanded; RenderSessions(true); });
                    disclosure.HorizontalContentAlignment = HorizontalAlignment.Left;
                    disclosure.Margin = new Thickness(0, 20 * NotchMetrics.Unit, 0, 0);
                    System.Windows.Automation.AutomationProperties.SetName(disclosure, expansion.Expanded ? "Show fewer tasks" : "Show all " + groups.Count + " tasks");
                    System.Windows.Automation.AutomationProperties.SetAutomationId(disclosure, expansion.Expanded ? "notch.sessions.showLess" : "notch.sessions.showAll");
                    list.Children.Add(disclosure);
                    if (focusDisclosure) disclosure.Focus();
                }
            }
            RenderSessions();
        }
        if (settings.ShowLastUpdated && reading?.UpdatedAt is { } updated) content.Children.Add(Text("Updated " + Age(updated), 9.5, Secondary));
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(1, (availableHeight ?? SystemParameters.WorkArea.Height) - 40) };
        var card = new Border { Width = NotchMetrics.CardWidth, CornerRadius = new CornerRadius(NotchMetrics.CardCorner),
            Background = Brushes.Black, Child = scroll, SnapsToDevicePixels = true };
        var layout = new Grid();
        var vertical = settings.Edge is NotchEdge.Right or NotchEdge.Left;
        if (vertical)
        {
            var tailFirst = settings.Edge == NotchEdge.Left;
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tailFirst ? NotchMetrics.Tail : NotchMetrics.CardWidth) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tailFirst ? NotchMetrics.CardWidth : NotchMetrics.Tail) });
            Grid.SetColumn(card, tailFirst ? 1 : 0); layout.Children.Add(card);
            var tail = new Polygon { Fill = Brushes.Black, Width = NotchMetrics.Tail, Height = 28, VerticalAlignment = VerticalAlignment.Center,
                Points = tailFirst ? new PointCollection([new(0, 14), new(NotchMetrics.Tail, 0), new(NotchMetrics.Tail, 28)]) : new PointCollection([new(0, 0), new(NotchMetrics.Tail, 14), new(0, 28)]) };
            Grid.SetColumn(tail, tailFirst ? 0 : 1); layout.Children.Add(tail);
        }
        else
        {
            var tailFirst = settings.Edge == NotchEdge.Top;
            layout.RowDefinitions.Add(new RowDefinition { Height = tailFirst ? new GridLength(NotchMetrics.Tail) : GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = tailFirst ? GridLength.Auto : new GridLength(NotchMetrics.Tail) });
            Grid.SetRow(card, tailFirst ? 1 : 0); layout.Children.Add(card);
            var tail = new Polygon { Fill = Brushes.Black, Width = 28, Height = NotchMetrics.Tail, HorizontalAlignment = HorizontalAlignment.Center,
                Points = tailFirst ? new PointCollection([new(14, 0), new(28, NotchMetrics.Tail), new(0, NotchMetrics.Tail)]) : new PointCollection([new(0, 0), new(28, 0), new(14, NotchMetrics.Tail)]) };
            Grid.SetRow(tail, tailFirst ? 0 : 1); layout.Children.Add(tail);
        }
        return layout;
    }
    private static void ConfigureSessionLine(Grid row)
    {
        row.Margin = new Thickness(0);
        foreach (var text in row.Children.OfType<TextBlock>())
        {
            text.FontSize = NotchMetrics.CardBodyFontSize; text.Margin = new Thickness(Grid.GetColumn(text) == 1 ? 8 : 0, 0, 0, 0);
            text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Height = 13; text.VerticalAlignment = VerticalAlignment.Center;
        }
    }
    internal static readonly Brush Secondary = Ui.Brush("#808080");
    internal static TextBlock Text(string value, double size = 10.5, Brush? brush = null, FontWeight? weight = null) =>
        new() { Text = value, FontSize = size, Foreground = brush ?? Brushes.White, FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
    internal static FrameworkElement Row(string label, string value, Brush? valueBrush = null)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = Text(label); left.TextTrimming = TextTrimming.CharacterEllipsis;
        var right = Text(value, 10.5, valueBrush ?? Secondary); right.Margin = new Thickness(8, 0, 0, 5);
        row.Children.Add(left); Grid.SetColumn(right, 1); row.Children.Add(right); return row;
    }
    internal static FrameworkElement UsageBar(double used, string accent = "#00FF88")
    {
        var amount = Math.Clamp(used, 0, 100);
        var fill = new Grid();
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(amount, GridUnitType.Star) });
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - amount, GridUnitType.Star) });
        fill.Children.Add(new Border { Background = Ui.Brush(NotchGeometry.BandColor(used, accent)), CornerRadius = new CornerRadius(2) });
        return new Border { Height = 4, Background = Ui.Brush("#2D2D2D"), CornerRadius = new CornerRadius(2), Child = fill, Margin = new Thickness(0, 0, 0, 6) };
    }
    private static System.Windows.Controls.Button PlainButton(string label, Action action)
    {
        var button = new System.Windows.Controls.Button { Content = Text(label, 10.5, Secondary),
            Style = (Style)System.Windows.Application.Current.FindResource("NotchButton"), Padding = new Thickness(2) };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action(); return button;
    }
    private static string Reset(DateTimeOffset? reset, string format) => ResetCopy.Text(reset, format, DateTimeOffset.Now);
    private static string Age(DateTimeOffset date)
    {
        var age = DateTimeOffset.Now - date;
        return age.TotalHours >= 1 ? (int)age.TotalHours + "h " + age.Minutes + "m ago" :
            age.TotalMinutes >= 1 ? (int)age.TotalMinutes + "m ago" : "just now";
    }
}
