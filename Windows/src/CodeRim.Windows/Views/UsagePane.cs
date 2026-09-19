using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
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
    private readonly StackPanel readings = new() { Margin = new Thickness(24) };
    private readonly StackPanel accountRow = new() { Margin = new Thickness(24, 0, 24, 16) };
    private readonly System.Windows.Controls.ComboBox selector;
    private readonly Stack<NavigationState> history = new();
    private sealed record NavigationState(string Destination, string Period, string Search, string? Project, string? Session, int VisibleRows);
    private void Forward(string target, string? selectedPeriod = null, string? selectedProject = null, string? selectedSession = null)
    {
        history.Push(new(destination, period, search, project, session, visibleRows));
        destination = target; period = selectedPeriod ?? period; project = selectedProject; session = selectedSession;
        BuildControls(); Update();
    }
    internal void Back()
    {
        if (!history.TryPop(out var previous)) return;
        (destination, period, search, project, session, visibleRows) = (previous.Destination, previous.Period, previous.Search, previous.Project, previous.Session, previous.VisibleRows);
        BuildControls(); Update();
    }
    internal bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (key == Key.OemOpenBrackets) { Back(); return true; }
        if (key is Key.D1 or Key.D2)
        {
            if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                var id = key == Key.D1 ? "codex" : "claude";
                if (settings.Current.EnabledProviders.Contains(id, StringComparer.Ordinal)) selector.SelectedValue = id;
            }
            else { mode = key == Key.D1 ? "Token usage" : "Limits"; destination = "overview"; history.Clear(); BuildControls(); Update(); }
            return true;
        }
        return false;
    }
    private readonly StackPanel controls = new();
    private readonly StackPanel filters = new() { Margin = new Thickness(24, 0, 24, 0) };
    private bool updatingChoices;
    internal void SelectProvider(string id)
    {
        if (settings.Current.EnabledProviders.Contains(id, StringComparer.Ordinal)) selector.SelectedValue = id;
    }
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
        var choices = settings.Current.EnabledProviders.Select(id => ProviderCatalog.Find(id)!).ToArray();
        if (!selector.Items.Cast<object>().SequenceEqual(choices))
        {
            updatingChoices = true;
            selector.ItemsSource = choices;
            var nextProvider = choices.Any(x => x.Id == provider) ? provider : choices.FirstOrDefault()?.Id ?? "codex";
            if (provider != nextProvider)
            {
                provider = nextProvider; destination = "overview"; project = session = null;
                period = "today"; search = ""; visibleRows = 40; history.Clear(); BuildControls();
                try { settings.Save(settings.Current with { UsageProvider = provider }); }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { }
            }
            selector.SelectedValue = provider;
            updatingChoices = false;
        }
        if (readings.IsKeyboardFocusWithin || accountRow.IsKeyboardFocusWithin) { pendingRefresh = true; return; }
        pendingRefresh = false; Update();
    }
    internal UsagePane(DashboardStore store, AppSettingsStore settings, string provider, Action<string?> navigate)
    {
        this.store = store; this.settings = settings; this.provider = provider; this.navigate = navigate;
        var choices = settings.Current.EnabledProviders.Select(id => ProviderCatalog.Find(id)!).ToArray();
        if (!choices.Any(x => x.Id == provider)) this.provider = choices.FirstOrDefault()?.Id ?? "codex";
        var header = new DockPanel { Margin = new Thickness(24, 20, 24, 24) };
        DockPanel.SetDock(controls, Dock.Right); header.Children.Add(controls);
        selector = new System.Windows.Controls.ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedValuePath = "Id",
            SelectedValue = this.provider, MinHeight = 24, Height = 24, MinWidth = 100, MaxWidth = 190, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(selector, "Usage provider");
        selector.SelectionChanged += (_, _) =>
        {
            if (!updatingChoices && selector.SelectedValue is string id)
            {
                this.provider = id; destination = "overview"; project = session = null; search = ""; period = "today"; visibleRows = 40; history.Clear();
                try { settings.Save(settings.Current with { UsageProvider = id }); }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { }
                BuildControls(); Update();
            }
        };
        header.Children.Add(selector); Children.Add(header);
        Children.Add(filters); Children.Add(accountRow); Children.Add(SettingsUi.Divider()); Children.Add(readings);
        LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (pendingRefresh && !readings.IsKeyboardFocusWithin && !accountRow.IsKeyboardFocusWithin) RefreshReadings(); }));
        BuildControls(); Update();
    }
    internal void ShowSessions() => Forward("sessions", "all-time");
    private void BuildControls()
    {
        controls.Children.Clear(); filters.Children.Clear();
        var bar = new WrapPanel();
        if (destination != "overview") bar.Children.Add(Ui.Button("‹ Back", Back));
        if (destination == "overview")
        {
            foreach (var choice in new[] { "Token usage", "Limits" })
            {
                var button = new RadioButton { Content = choice == "Limits" ? (provider == "codex" ? "Codex Limits" : provider == "claude" ? "Claude Limits" : "Limits") : "Token Usage",
                    IsChecked = mode == choice, GroupName = "UsageMode", Style = (Style)System.Windows.Application.Current.FindResource("UsageModeButton") };
                System.Windows.Automation.AutomationProperties.SetName(button, button.Content.ToString());
                button.Checked += (_, _) => { mode = choice; Update(); }; bar.Children.Add(button);
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
        if (destination is "projects" or "sessions" && project is null && session is null)
        {
            var filter = new System.Windows.Controls.TextBox { Text = search, Padding = new Thickness(8), Margin = new Thickness(0, 10, 0, 12), ToolTip = "Filter " + destination };
            System.Windows.Automation.AutomationProperties.SetName(filter, "Filter " + destination);
            filter.TextChanged += (_, _) => { search = filter.Text; visibleRows = 40; Update(); };
            filters.Children.Add(filter);
        }
    }
    internal void Update()
    {
        accountRow.Children.Clear();
        var identity = SavedAccounts.CurrentAccountLabel(provider, store.Synthetic);
        var account = new DockPanel();
        var change = Ui.Button("Switch", () => navigate(provider is "codex" or "claude" ? provider + "-accounts" : provider));
        change.MinHeight = 20; change.Height = 20; change.Padding = new Thickness(0); change.Background = Brushes.Transparent; change.BorderThickness = new Thickness(0); change.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(change, Dock.Right); account.Children.Add(change);
        var accountLabel = Ui.Text(identity ?? store.Readings.GetValueOrDefault(provider)?.Plan ?? "Account", 12);
        accountLabel.FontWeight = FontWeights.SemiBold; accountLabel.Margin = new Thickness(0); accountLabel.TextWrapping = TextWrapping.NoWrap; accountLabel.TextTrimming = TextTrimming.CharacterEllipsis; accountLabel.ToolTip = identity; accountLabel.VerticalAlignment = VerticalAlignment.Center;
        account.Children.Add(accountLabel); accountRow.Children.Add(account);
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
        readings.Children.Add(Heading("Today"));
        var overview = new Grid { Margin = new Thickness(0, 18, 0, 24) };
        overview.ColumnDefinitions.Add(new ColumnDefinition()); overview.ColumnDefinitions.Add(new ColumnDefinition());
        var total = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        total.Children.Add(Ui.Text(TokenFormatter.Format(today.TotalTokens, settings.Current.NumberStyle), 42, weight: FontWeights.SemiBold));
        total.Children.Add(Ui.Text("tokens", 13, "#A6A6AA"));
        if (snapshot.Quality != DataQuality.Exact) total.Children.Add(Ui.Text(snapshot.Quality == DataQuality.Partial ? "Partial local reading" : "No local usage observed", 11, "#A6A6AA"));
        overview.Children.Add(total);
        var breakdown = new StackPanel(); Breakdown(breakdown, today); Grid.SetColumn(breakdown, 1); overview.Children.Add(breakdown); readings.Children.Add(overview);
        readings.Children.Add(SettingsUi.Divider());
        readings.Children.Add(Heading("History"));
        var history = new Grid { Margin = new Thickness(0, 14, 0, 24) };
        var values = new[] { ("This Week", "week", snapshot.Week), ("This Month", "month", snapshot.Month), ("Local History", "all-time", snapshot.AllTime) };
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i]; history.ColumnDefinitions.Add(new ColumnDefinition());
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            var periodLabel = new DockPanel();
            var arrow = Ui.Text("›", 15, "#A6A6AA"); DockPanel.SetDock(arrow, Dock.Right); periodLabel.Children.Add(arrow);
            periodLabel.Children.Add(Ui.Text(value.Item1, 13, "#A6A6AA")); panel.Children.Add(periodLabel);
            panel.Children.Add(Ui.Text(TokenFormatter.Format(value.Item3.TotalTokens, settings.Current.NumberStyle), 21, weight: FontWeights.SemiBold));
            var button = Ui.Button("", () => Forward("activity", value.Item2));
            System.Windows.Automation.AutomationProperties.SetName(button, value.Item1 + ": " + value.Item3.TotalTokens.ToString(CultureInfo.CurrentCulture) + " tokens");
            button.Content = panel; button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); button.Margin = new Thickness(0);
            if (i > 0) { var separator = new Border { Width = 1, Height = 64, Background = (Brush)System.Windows.Application.Current.FindResource("DividerBrush"), HorizontalAlignment = HorizontalAlignment.Left }; Grid.SetColumn(separator, i); history.Children.Add(separator); }
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(i == 0 ? 0 : 16, 0, 16, 0); Grid.SetColumn(button, i); history.Children.Add(button);
        }
        readings.Children.Add(history);
        var links = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 24, 0, 18) };
        foreach (var (id, label, detail, icon) in new[]
        {
            ("activity", "Usage", "Daily tokens, model breakdown and estimated API cost", "M2,14 V8 M8,14 V2 M14,14 V5"),
            ("projects", "Projects", "Usage grouped by local project", "M1,4 V13 Q1,15 3,15 H13 Q15,15 15,13 V5 Q15,3 13,3 H7 L5,1 H3 Q1,1 1,3 Z"),
            ("sessions", "Sessions", "Individual coding sessions and their models", "M1,1 H15 V11 H8 L4,15 V11 H1 Z M4,4 H12 M4,7 H10")
        })
        {
            if (!settings.Current.AnalyticsEnabled || id == "projects" && !settings.Current.ProjectsEnabled || id == "sessions" && !settings.Current.SessionsEnabled) continue;
            var link = Ui.Button(label, () => Forward(id, "week"));
            System.Windows.Automation.AutomationProperties.SetAutomationId(link, "usage.destination." + id);
            link.ToolTip = detail; link.BorderThickness = new Thickness(0); link.SetResourceReference(Control.BackgroundProperty, "ControlBackground");
            link.Padding = new Thickness(12, 11, 12, 11); link.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var content = new DockPanel();
            var mark = new System.Windows.Shapes.Path { Data = Geometry.Parse(icon), Stroke = (Brush)System.Windows.Application.Current.FindResource("SecondaryText"), StrokeThickness = 1.2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(mark, Dock.Left); content.Children.Add(mark);
            var disclosure = Ui.Text("›", 16, "#98989D"); disclosure.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(disclosure, Dock.Right); content.Children.Add(disclosure);
            var title = Ui.Text(label, 13); title.Margin = new Thickness(0); title.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(title);
            link.Content = content; links.Children.Add(link);
        }
        if (links.Children.Count > 0) { links.Columns = links.Children.Count; readings.Children.Add(SettingsUi.Divider()); readings.Children.Add(links); }
        if (settings.Current.ShowLastUpdated || store.IsRefreshing)
            readings.Children.Add(Ui.Text(store.IsRefreshing ? "Refreshing…" : store.Status, 11, "#808080"));
    }
    private static DockPanel Heading(string title)
    {
        var row = new DockPanel { Margin = new Thickness(0, title == "History" ? 24 : 0, 0, 0) };
        var scope = Ui.Text("This PC", 11, "#A6A6AA"); scope.ToolTip = "Local usage across accounts on this computer."; DockPanel.SetDock(scope, Dock.Right); row.Children.Add(scope);
        row.Children.Add(Ui.Text(title, 13, weight: FontWeights.SemiBold)); return row;
    }
    private void Limits()
    {
        var reading = ProviderDisplayPolicy.Apply(store.Readings.GetValueOrDefault(provider)?.Evaluated(DateTimeOffset.Now), settings.Current);
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
        parent.ToolTip = "Cached input is already included in Input. Total equals Input plus Output.";
    }
    private IEnumerable<UsageEvent> Filter(bool includeSelection = true)
    {
        var now = DateTimeOffset.Now; var day = DateTime.Today;
        var since = period switch { "today" => day, "week" => day.AddDays(-(((int)day.DayOfWeek + (settings.Current.WeekStart == WeekStart.Monday ? 6 : 0)) % 7)), "month" => new DateTime(day.Year, day.Month, 1), _ => DateTime.MinValue };
        return (store.Events.GetValueOrDefault(provider) ?? []).Where(x => x.OccurredAt <= now && x.OccurredAt.LocalDateTime >= since
            && (!includeSelection || (project is null || x.ProjectId == project) && (session is null || x.SessionId == session)));
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
                var button = Ui.Button("", () => Forward(destination, selectedProject: destination == "projects" ? group.Id : null, selectedSession: destination == "sessions" ? group.Id : null));
                System.Windows.Automation.AutomationProperties.SetName(button, group.Name + ": " + group.Total.ToString(CultureInfo.CurrentCulture) + " tokens");
                button.Content = Ui.Row(group.Name, TokenFormatter.Format(group.Total, settings.Current.NumberStyle) + "  ›");
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch; readings.Children.Add(button);
            }
            if (groups.Length > visibleRows) readings.Children.Add(Ui.Button("Show more (" + (groups.Length - visibleRows) + " remaining)", () => { visibleRows += 40; Update(); }));
            return;
        }
        if (session is not null)
        {
            var detail = store.SessionDetails.GetValueOrDefault(provider)?.FirstOrDefault(x => x.Id == session);
            if (settings.Current.AgentDetailsEnabled)
            {
                var visibleSessions = Filter(includeSelection: false).Select(x => x.SessionId).ToHashSet(StringComparer.Ordinal);
                var children = (store.SessionDetails.GetValueOrDefault(provider) ?? []).Where(x => x.ParentId == session && visibleSessions.Contains(x.Id)).ToArray();
                readings.Children.Add(Ui.Row("Direct sub-agents", children.Length.ToString(CultureInfo.CurrentCulture)));
                foreach (var child in children)
                {
                    var target = child.Id;
                    readings.Children.Add(Ui.Button("Sub-agent " + target[..Math.Min(12, target.Length)],
                        () => Forward("sessions", selectedSession: target)));
                }
                if (children.Length > 0) readings.Children.Add(Ui.Text("Sub-agent tokens are separate from this total.", 11, "#A6A6AA"));
            }
            if (settings.Current.AttachmentMetadataEnabled && provider == "codex")
            {
                readings.Children.Add(Ui.Row("Whole-session images", detail is null ? "Unavailable" : detail.Attachments.Sum(x => (long)x.Count).ToString(CultureInfo.CurrentCulture)));
                readings.Children.Add(Ui.Text("Whole-session metadata after the history cutoff; image contents are never stored. Local metadata may be incomplete.", 11, "#A6A6AA"));
            }
        }
        var totals = events.Aggregate(TokenUsage.Zero, (sum, x) => sum.Add(x.Usage)); Breakdown(readings, totals);
        var cost = UsageAnalytics.Estimate(events);
        if (settings.Current.CostEstimatesEnabled)
        {
        Ui.Section(readings, cost.Label);
        readings.Children.Add(Ui.Text(cost.Amount is { } amount ? "$" + amount.ToString("N4", CultureInfo.CurrentCulture) : "Unavailable", 24));
        readings.Children.Add(Ui.Text("Estimated from bundled API pricing · not a bill", 11, "#A6A6AA"));
        if (cost.IsPartial) readings.Children.Add(Ui.Text($"Excludes {cost.ExcludedTokens:N0} tokens · " + string.Join(", ", cost.ExcludedModels), 11, "#A6A6AA"));
        }
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
