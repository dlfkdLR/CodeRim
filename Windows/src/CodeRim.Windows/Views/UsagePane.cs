using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

/// <summary>Persistent selectors and navigation; polling replaces only the numeric content.</summary>
internal sealed class UsagePane : StackPanel
{
    private readonly DashboardStore store;
    private readonly AppSettingsStore settings;
    private readonly Action<string?> navigate;
    private readonly StackPanel readings = new();
    private readonly StackPanel controls = new();
    private string provider;
    private string period = "today";
    private string destination = "overview";
    private string mode = "Token usage";
    private string search = "";
    private string? project;
    private string? session;
    private int visibleRows = 40;
    private bool pendingRefresh;
    internal void RefreshReadings()
    {
        if (readings.IsKeyboardFocusWithin) { pendingRefresh = true; return; }
        pendingRefresh = false; Update();
    }
    internal UsagePane(DashboardStore store, AppSettingsStore settings, string provider, Action<string?> navigate)
    {
        this.store = store; this.settings = settings; this.provider = provider; this.navigate = navigate;
        var choices = settings.Current.EnabledProviders.Select(id => ProviderCatalog.Find(id)!).ToArray();
        if (!choices.Any(x => x.Id == provider)) this.provider = choices.FirstOrDefault()?.Id ?? "codex";
        Children.Add(Ui.Text("Usage", 24, weight: FontWeights.SemiBold));
        var header = new DockPanel { Margin = new Thickness(0, 8, 0, 12) };
        var refresh = Ui.AsyncButton("Refresh", () => store.RefreshAsync(true));
        DockPanel.SetDock(refresh, Dock.Right); header.Children.Add(refresh);
        var select = new System.Windows.Controls.ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedValuePath = "Id",
            SelectedValue = this.provider, Width = 190, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(select, "Usage provider");
        select.SelectionChanged += (_, _) => { if (select.SelectedValue is string id) { this.provider = id; destination = "overview"; project = session = null; BuildControls(); Update(); } };
        header.Children.Add(select); Children.Add(header);
        Children.Add(controls); Children.Add(readings);
        readings.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (pendingRefresh && !readings.IsKeyboardFocusWithin) RefreshReadings(); }));
        BuildControls(); Update();
    }
    internal void ShowSessions() { destination = "sessions"; period = "all-time"; BuildControls(); Update(); }
    private void BuildControls()
    {
        controls.Children.Clear();
        var bar = new WrapPanel();
        if (destination != "overview") bar.Children.Add(Ui.Button("‹ Overview", () => { destination = "overview"; project = session = null; BuildControls(); Update(); }));
        if (destination == "overview")
        {
            foreach (var choice in new[] { "Token usage", "Limits" })
            {
                var button = Ui.Button(choice, () => { mode = choice; BuildControls(); Update(); });
                button.Background = Ui.Brush(mode == choice ? "#535356" : "#303030"); bar.Children.Add(button);
            }
        }
        else
        {
            var periods = new[] { ("today", "Today"), ("week", "This week"), ("month", "This month"), ("all-time", "All time") };
            var select = new System.Windows.Controls.ComboBox { ItemsSource = periods.Select(x => new PeriodChoice(x.Item1, x.Item2)), DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = period, MinWidth = 145, Margin = new Thickness(0, 4, 8, 4) };
            System.Windows.Automation.AutomationProperties.SetName(select, "Usage period");
            select.SelectionChanged += (_, _) => { if (select.SelectedValue is string value) { period = value; visibleRows = 40; Update(); } }; bar.Children.Add(select);
        }
        controls.Children.Add(bar);
        if (destination is "projects" or "sessions")
        {
            var filter = new System.Windows.Controls.TextBox { Text = search, Padding = new Thickness(8), Margin = new Thickness(0, 10, 0, 12), ToolTip = "Filter " + destination };
            System.Windows.Automation.AutomationProperties.SetName(filter, "Filter " + destination);
            filter.TextChanged += (_, _) => { search = filter.Text; visibleRows = 40; Update(); };
            controls.Children.Add(filter);
        }
    }
    internal void Update()
    {
        readings.Children.Clear();
        if (destination != "overview") { Detail(); return; }
        if (mode == "Limits") { Limits(); return; }
        if (provider is not ("codex" or "claude"))
        {
            readings.Children.Add(Ui.Text("Local token history is available for Codex and Claude Code.", color: "#A6A6AA"));
            readings.Children.Add(Ui.Button("View provider limits", () => { mode = "Limits"; BuildControls(); Update(); })); return;
        }
        var snapshot = store.Usage.GetValueOrDefault(provider) ?? UsageSnapshot.Empty;
        var today = snapshot.Today;
        readings.Children.Add(Ui.Text("Today · This PC · Across accounts", 12, "#A6A6AA"));
        var overview = new Grid { Margin = new Thickness(0, 12, 0, 22) };
        overview.ColumnDefinitions.Add(new ColumnDefinition()); overview.ColumnDefinitions.Add(new ColumnDefinition());
        var total = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        total.Children.Add(Ui.Text(TokenFormatter.Format(today.TotalTokens, settings.Current.NumberStyle), 40, weight: FontWeights.SemiBold));
        total.Children.Add(Ui.Text("tokens", 13, "#A6A6AA"));
        if (snapshot.Quality != DataQuality.Exact) total.Children.Add(Ui.Text(snapshot.Quality == DataQuality.Partial ? "Partial local reading" : "No local usage observed", 11, "#A6A6AA"));
        overview.Children.Add(total);
        var breakdown = new StackPanel(); Breakdown(breakdown, today); Grid.SetColumn(breakdown, 1); overview.Children.Add(breakdown); readings.Children.Add(overview);
        var history = new Grid();
        var values = new[] { ("This week", "week", snapshot.Week), ("This month", "month", snapshot.Month), ("All time", "all-time", snapshot.AllTime) };
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i]; history.ColumnDefinitions.Add(new ColumnDefinition());
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(Ui.Text(value.Item1, 11, "#A6A6AA"));
            panel.Children.Add(Ui.Text(TokenFormatter.Format(value.Item3.TotalTokens, settings.Current.NumberStyle), 21, weight: FontWeights.SemiBold));
            var button = Ui.Button("", () => { period = value.Item2; destination = "activity"; BuildControls(); Update(); });
            System.Windows.Automation.AutomationProperties.SetName(button, value.Item1 + ": " + value.Item3.TotalTokens.ToString(CultureInfo.CurrentCulture) + " tokens");
            button.Content = panel; button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); button.Margin = new Thickness(0);
            if (i > 0) { var separator = new Border { Width = 1, Height = 36, Background = Ui.Brush("#38383A"), HorizontalAlignment = HorizontalAlignment.Left }; Grid.SetColumn(separator, i); history.Children.Add(separator); }
            button.HorizontalContentAlignment = HorizontalAlignment.Left; Grid.SetColumn(button, i); history.Children.Add(button);
        }
        readings.Children.Add(history);
        var links = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 24, 0, 18) };
        foreach (var (id, label, detail, icon) in new[]
        {
            ("activity", "Usage history", "Daily tokens, model breakdown and estimated API cost", "M2,14 V8 M8,14 V2 M14,14 V5"),
            ("projects", "Projects", "Usage grouped by local project", "M1,4 V13 Q1,15 3,15 H13 Q15,15 15,13 V5 Q15,3 13,3 H7 L5,1 H3 Q1,1 1,3 Z"),
            ("sessions", "Sessions", "Individual coding sessions and their models", "M1,1 H15 V11 H8 L4,15 V11 H1 Z M4,4 H12 M4,7 H10")
        })
        {
            var link = Ui.Button(label, () => { destination = id; period = "week"; search = ""; visibleRows = 40; BuildControls(); Update(); });
            System.Windows.Automation.AutomationProperties.SetAutomationId(link, "usage.destination." + id);
            link.ToolTip = detail; link.BorderThickness = new Thickness(0); link.Background = Ui.Brush("#303030");
            link.Padding = new Thickness(12, 11, 12, 11); link.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var content = new DockPanel();
            var mark = new System.Windows.Shapes.Path { Data = Geometry.Parse(icon), Stroke = Ui.Brush("#C6C6CA"), StrokeThickness = 1.2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(mark, Dock.Left); content.Children.Add(mark);
            var disclosure = Ui.Text("›", 16, "#98989D"); disclosure.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(disclosure, Dock.Right); content.Children.Add(disclosure);
            var title = Ui.Text(label, 13); title.Margin = new Thickness(0); title.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(title);
            link.Content = content; links.Children.Add(link);
        }
        readings.Children.Add(links);
        if (settings.Current.ShowLastUpdated || store.IsRefreshing)
            readings.Children.Add(Ui.Text(store.IsRefreshing ? "Refreshing…" : store.Status, 11, "#808080"));
    }
    private void Limits()
    {
        var reading = store.Readings.GetValueOrDefault(provider)?.Evaluated(DateTimeOffset.Now);
        readings.Children.Add(Ui.Text(reading?.Plan ?? ProviderCatalog.Find(provider)?.Name ?? provider, 18, weight: FontWeights.SemiBold));
        foreach (var window in reading?.Windows ?? [])
        {
            Ui.Section(readings, window.Name);
            if (window.UsedPercent is { } used)
            {
                readings.Children.Add(Ui.Row("Used", used.ToString("0.#", CultureInfo.CurrentCulture) + "%"));
                readings.Children.Add(NotchPopover.UsageBar(used));
            }
            if (window.DisplayValue is { } value) readings.Children.Add(Ui.Text(value));
            if (window.UsedCount is { } count) readings.Children.Add(Ui.Row("Used", count.ToString("N0", CultureInfo.CurrentCulture) + " " + window.Unit));
            if (window.RemainingCount is { } remaining) readings.Children.Add(Ui.Row("Remaining", remaining.ToString("N0", CultureInfo.CurrentCulture) + " " + window.Unit));
            if (window.ResetsAt is { } reset) readings.Children.Add(Ui.Row("Resets", reset.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        }
        readings.Children.Add(Ui.Text(reading?.Message ?? (reading is null ? "Waiting for a reading…" : reading.State.ToString()), 12, "#A6A6AA"));
        readings.Children.Add(Ui.Button("Manage connection", () => navigate(provider)));
    }
    private void Breakdown(Panel parent, TokenUsage tokens)
    {
        parent.Children.Add(Ui.Row("Input", TokenFormatter.Format(tokens.InputTokens, settings.Current.NumberStyle)));
        if (settings.Current.ShowCachedInput)
        {
            parent.Children.Add(Ui.Row("Cached input", TokenFormatter.Format(tokens.CachedInputTokens, settings.Current.NumberStyle)));
            if (tokens.CacheWriteInputTokens is { } written && written > 0) parent.Children.Add(Ui.Row("Cache writes", TokenFormatter.Format(written, settings.Current.NumberStyle)));
        }
        parent.Children.Add(Ui.Row("Output", TokenFormatter.Format(tokens.OutputTokens, settings.Current.NumberStyle)));
        if (settings.Current.ShowCachedInput) parent.Children.Add(Ui.Text("Cache tokens are included in Input.", 10, "#808080"));
    }
    private IEnumerable<UsageEvent> Filter()
    {
        var now = DateTimeOffset.Now; var day = DateTime.Today;
        var since = period switch { "today" => day, "week" => day.AddDays(-(((int)day.DayOfWeek + (settings.Current.WeekStart == WeekStart.Monday ? 6 : 0)) % 7)), "month" => new DateTime(day.Year, day.Month, 1), _ => DateTime.MinValue };
        return (store.Events.GetValueOrDefault(provider) ?? []).Where(x => x.OccurredAt <= now && x.OccurredAt.LocalDateTime >= since
            && (project is null || x.ProjectId == project) && (session is null || x.SessionId == session));
    }
    private void Detail()
    {
        var events = Filter().ToArray();
        readings.Children.Add(Ui.Text(session is not null ? "Session details" : project is not null ? "Project details" : destination switch { "projects" => "Projects", "sessions" => "Sessions", _ => "Usage history" }, 20, weight: FontWeights.SemiBold));
        if (events.Length == 0) { readings.Children.Add(Ui.Text("No local usage observed for this period.", color: "#A6A6AA")); return; }
        if (destination is "projects" or "sessions" && project is null && session is null)
        {
            var groups = events.GroupBy(x => destination == "projects" ? x.ProjectId : x.SessionId).Select(g => new { Id = g.Key, Name = destination == "projects" ? g.First().Project : g.Key, Total = g.Aggregate(TokenUsage.Zero, (sum, x) => sum.Add(x.Usage)).TotalTokens })
                .Where(g => g.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).OrderByDescending(g => g.Total).ToArray();
            foreach (var group in groups.Take(visibleRows))
            {
                var button = Ui.Button("", () => { if (destination == "projects") project = group.Id; else session = group.Id; BuildControls(); Update(); });
                System.Windows.Automation.AutomationProperties.SetName(button, group.Name + ": " + group.Total.ToString(CultureInfo.CurrentCulture) + " tokens");
                button.Content = Ui.Row(group.Name, TokenFormatter.Format(group.Total, settings.Current.NumberStyle) + "  ›");
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch; readings.Children.Add(button);
            }
            if (groups.Length > visibleRows) readings.Children.Add(Ui.Button("Show more (" + (groups.Length - visibleRows) + " remaining)", () => { visibleRows += 40; Update(); }));
            return;
        }
        var totals = events.Aggregate(TokenUsage.Zero, (sum, x) => sum.Add(x.Usage)); Breakdown(readings, totals);
        var cost = UsageAnalytics.Estimate(events);
        Ui.Section(readings, cost.Label);
        readings.Children.Add(Ui.Text(cost.Amount is { } amount ? "$" + amount.ToString("N4", CultureInfo.CurrentCulture) : "Unavailable", 24));
        readings.Children.Add(Ui.Text("Estimated from bundled API pricing · not a bill", 11, "#A6A6AA"));
        if (cost.IsPartial) readings.Children.Add(Ui.Text($"Excludes {cost.ExcludedTokens:N0} tokens · " + string.Join(", ", cost.ExcludedModels), 11, "#A6A6AA"));
        Ui.Section(readings, "Daily history");
        var days = UsageAnalytics.Group(events, "day").OrderBy(x => x.Name, StringComparer.Ordinal).TakeLast(30).ToArray();
        var chart = new Grid { Height = 96, Margin = new Thickness(0, 8, 0, 12) }; var maximum = Math.Max(1, days.Max(x => x.Tokens));
        for (var i = 0; i < days.Length; i++)
        {
            chart.ColumnDefinitions.Add(new ColumnDefinition());
            var label = $"{days[i].Name}: {days[i].Tokens:N0} tokens";
            var bar = new Border { Background = Ui.Brush("#0A84FF"), VerticalAlignment = VerticalAlignment.Bottom, Height = Math.Max(2, days[i].Tokens / (double)maximum * 96), CornerRadius = new CornerRadius(2), Margin = new Thickness(2, 0, 2, 0), ToolTip = label };
            System.Windows.Automation.AutomationProperties.SetName(bar, label); Grid.SetColumn(bar, i); chart.Children.Add(bar);
        }
        readings.Children.Add(chart); Ui.Section(readings, "Models");
        foreach (var row in UsageAnalytics.Group(events, "model")) readings.Children.Add(Ui.Row(row.Name, TokenFormatter.Format(row.Tokens, settings.Current.NumberStyle)));
    }
    private sealed record PeriodChoice(string Id, string Name);
}
