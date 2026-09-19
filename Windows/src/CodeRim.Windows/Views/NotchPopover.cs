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
internal static class NotchPopover
{
    internal static FrameworkElement Create(string id, DashboardStore store, AppSettings settings, Action<string?> navigate)
    {
        var content = new StackPanel { Margin = new Thickness(NotchMetrics.CardPadding) };
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        var mark = new ProviderMark { ProviderId = id, Width = 18, Height = 18, Margin = new Thickness(0, 0, 7, 0) };
        header.Children.Add(mark);
        header.Children.Add(Text(ProviderCatalog.Find(id)?.Name ?? id, 13.7, Brushes.White, FontWeights.SemiBold));
        content.Children.Add(header);
        var reading = ProviderDisplayPolicy.Apply(store.Readings.GetValueOrDefault(id)?.Evaluated(DateTimeOffset.Now), settings);
        var account = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        var switcher = PlainButton("Switch account", () => navigate(id is "codex" or "claude" ? id + "-accounts" : id));
        switcher.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(switcher, Dock.Right);
        account.Children.Add(switcher);
        var identity = SavedAccounts.CurrentAccountLabel(id, store.Synthetic);
        account.Children.Add(Text(reading?.Plan ?? "Account", 10.5, Secondary));
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
        foreach (var window in reading?.Windows ?? [])
        {
            content.Children.Add(Row(window.Name, Reset(window.ResetsAt, settings.ResetTime)));
            if (window.UsedPercent is { } percent)
            {
                content.Children.Add(UsageBar(percent));
                content.Children.Add(Text(percent.ToString("0.#", CultureInfo.CurrentCulture) + "% used · " +
                    Math.Clamp(100 - percent, 0, 100).ToString("0.#", CultureInfo.CurrentCulture) + "% left", 10.5, Secondary));
                if (settings.ShowUsagePace && window.DurationMinutes > 0 && window.ResetsAt is { } reset)
                {
                    var elapsed = Math.Clamp(1 - (reset - DateTimeOffset.Now).TotalMinutes / window.DurationMinutes, 0, 1) * 100;
                    content.Children.Add(Text(percent > elapsed + 5 ? "Above even pace" : "Within even pace", 10.5, Secondary));
                }
            }
            if (window.DisplayValue is { } display) content.Children.Add(Text(display, 10.5));
            if (window.UsedCount is { } count) content.Children.Add(Text(TokenFormatter.Format(count, settings.NumberStyle) + " " + (window.Unit ?? "units") + " used", 10.5));
            if (window.RemainingCount is { } remaining) content.Children.Add(Text(TokenFormatter.Format(remaining, settings.NumberStyle) + " " + (window.Unit ?? "units") + " left", 10.5));
        }
        if (reading is null) content.Children.Add(Text("Waiting for a reading…", 10.5, Secondary));
        else if (reading.State != ReadingState.Ready)
        {
            content.Children.Add(Text(reading.Message ?? reading.State.ToString(), 10.5, Ui.Brush("#F2FF00")));
            if (reading.State == ReadingState.NeedsAuth) content.Children.Add(PlainButton("Connect " + (ProviderCatalog.Find(id)?.Name ?? id), () => navigate(id)));
        }
        var sessions = store.Sessions.Where(x => x.Provider == id).ToArray();
        if (sessions.Length > 0)
        {
            content.Children.Add(new Border { Height = 1, Background = Ui.Brush("#303030"), Margin = new Thickness(0, 8, 0, 8) });
            foreach (var session in sessions.Take(6))
            {
                var state = session.State switch { "busy" => "working", "waiting" => "waiting", _ => "idle" };
                content.Children.Add(Row(session.Name, state, session.State == "busy" ? Ui.Brush("#00FF88") : Secondary));
                content.Children.Add(Text(Age(session.Since), 9.5, Secondary));
            }
            if (sessions.Length > 6) content.Children.Add(PlainButton("View all " + sessions.Length + " sessions", () => navigate("sessions:" + id)));
        }
        if (settings.ShowLastUpdated && reading?.UpdatedAt is { } updated) content.Children.Add(Text("Updated " + Age(updated), 9.5, Secondary));
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(160, SystemParameters.WorkArea.Height - 40) };
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
    internal static FrameworkElement UsageBar(double used)
    {
        var amount = Math.Clamp(used, 0, 100);
        var fill = new Grid();
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(amount, GridUnitType.Star) });
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - amount, GridUnitType.Star) });
        fill.Children.Add(new Border { Background = Ui.Brush(NotchGeometry.BandColor(used)), CornerRadius = new CornerRadius(2) });
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
