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
internal sealed partial class UsagePane : StackPanel
{
    private readonly DashboardStore store;
    private readonly AppSettingsStore settings;
    private readonly Action<string?> navigate;
    private readonly StackPanel readings = new() { Margin = new Thickness(24, 16, 24, 16) };
    private readonly StackPanel accountRow = new() { Margin = new Thickness(24, 0, 24, 4), MinHeight = 44 };
    private readonly System.Windows.Controls.ComboBox selector;
    private readonly Grid header;
    private System.Windows.Controls.Button? refreshAction;
    private TextBlock? detailTitle;
    private readonly Stack<NavigationState> history = new();
    private sealed record NavigationState(string Destination, string Period, string Search, string? Project, string? Session, int VisibleRows, string? Model, DateTimeOffset? Bucket);
    private void Forward(string target, string? selectedPeriod = null, string? selectedProject = null, string? selectedSession = null, string? model = null)
    {
        history.Push(new(destination, period, search, project, session, visibleRows, selectedModel, selectedBucket));
        destination = target; period = selectedPeriod ?? period; project = selectedProject; session = selectedSession; selectedModel = model; selectedBucket = null;
        BuildControls(); Update();
    }
    internal void Back()
    {
        if (!history.TryPop(out var previous)) return;
        (destination, period, search, project, session, visibleRows, selectedModel, selectedBucket) = (previous.Destination, previous.Period, previous.Search, previous.Project, previous.Session, previous.VisibleRows, previous.Model, previous.Bucket);
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
    private string? lastView;
    private readonly Dictionary<string, AnimatedMetric> metricValues = [];
    private AnimatedMetric Metric(string key, long value, double size)
    {
        key = provider + ":" + key;
        if (!metricValues.TryGetValue(key, out var metric))
        {
            metric = new AnimatedMetric(value, value, settings.Current.NumberStyle, size, false);
            metricValues[key] = metric;
        }
        // Polling can emit several notifications in one dispatcher turn. Reuse the actual
        // displayed metric so a duplicate notification cannot replace an in-flight reading.
        if (metric.Parent is Panel panel) panel.Children.Remove(metric);
        else if (metric.Parent is Viewbox viewbox) viewbox.Child = null;
        metric.Update(value, settings.Current.NumberStyle, IsLoaded && !settings.Current.ReduceMotion);
        return metric;
    }
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
        header = new Grid { Margin = new Thickness(24, 16, 24, 6) };
        header.Children.Add(controls);
        selector = new System.Windows.Controls.ComboBox { ItemsSource = choices, ItemTemplate = ProviderTemplate(), SelectedValuePath = "Id",
            SelectedValue = this.provider, MinHeight = 34, Height = 34, Width = 142, MaxWidth = 190, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)System.Windows.Application.Current.FindResource("UsageProviderPicker") };
        TextSearch.SetTextPath(selector, "Name");
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
        header.Children.Add(selector); AdaptHeader(header, controls); Children.Add(header);
        Children.Add(filters); Children.Add(accountRow); Children.Add(SettingsUi.Divider()); Children.Add(readings);
        LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (pendingRefresh && !readings.IsKeyboardFocusWithin && !accountRow.IsKeyboardFocusWithin) RefreshReadings(); }));
        BuildControls(); Update();
    }
    private string DetailTitle() => destination switch
    {
        "model" => "Model",
        "projects" => project is null ? "Projects" : "Project",
        "sessions" => session is null ? "Sessions" : "Session",
        _ => period switch { "week" => "This Week", "month" => "This Month", "all-time" => "Local History", "today" => "Today", _ => "Usage" }
    };
    private static DataTemplate ProviderTemplate()
    {
        var row = new FrameworkElementFactory(typeof(DockPanel));
        var glyph = new FrameworkElementFactory(typeof(ProviderMark));
        glyph.SetValue(DockPanel.DockProperty, Dock.Left);
        glyph.SetValue(WidthProperty, 17d); glyph.SetValue(HeightProperty, 17d);
        glyph.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        glyph.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        glyph.SetBinding(ProviderMark.ProviderIdProperty, new System.Windows.Data.Binding("Id"));
        glyph.SetBinding(ProviderMark.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
        { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1) });
        row.AppendChild(glyph);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
        name.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
        { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1) });
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(name);
        return new DataTemplate { VisualTree = row };
    }
    internal void ShowSessions() => Forward("sessions", "all-time");
    private void BuildControls()
    {
        controls.Children.Clear(); filters.Children.Clear(); detailTitle = null;
        var bar = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        selector.Visibility = destination == "overview" ? Visibility.Visible : Visibility.Collapsed;
        accountRow.Visibility = destination == "overview" ? Visibility.Visible : Visibility.Collapsed;
        header.Margin = destination == "overview" ? new Thickness(24, 16, 24, 6) : new Thickness(18, 8, 18, 8);
        if (destination != "overview")
        {
            var detail = new DockPanel { LastChildFill = true, MinHeight = 28 };
            var back = Ui.Button("‹", Back); back.Width = back.Height = 28; back.Padding = new Thickness(0);
            back.Margin = new Thickness(0, 0, 8, 0); back.Background = Brushes.Transparent; back.BorderThickness = new Thickness(0);
            back.ToolTip = "Back (Ctrl+[)";
            System.Windows.Automation.AutomationProperties.SetName(back, "Back");
            System.Windows.Automation.AutomationProperties.SetAutomationId(back, "usage.navigation.back");
            DockPanel.SetDock(back, Dock.Left); detail.Children.Add(back);
            var context = Ui.Text(ProviderCatalog.Find(provider)?.Name ?? provider, 11, "#A6A6AA");
            context.VerticalAlignment = VerticalAlignment.Center; context.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(context, Dock.Right); detail.Children.Add(context);
            var heading = Ui.Text(DetailTitle(), 13, weight: FontWeights.SemiBold); heading.Margin = new Thickness(0); heading.VerticalAlignment = VerticalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetAutomationId(heading, "usage.detail.title"); detailTitle = heading; detail.Children.Add(heading);
            controls.Children.Add(detail);
        }
        if (destination == "overview")
        {
            var segments = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            var group = new Border { Child = segments, CornerRadius = new CornerRadius(6), Padding = new Thickness(2), Height = 28, Width = 246, VerticalAlignment = VerticalAlignment.Center };
            group.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            System.Windows.Automation.AutomationProperties.SetAutomationId(group, "usage.mode");
            System.Windows.Automation.AutomationProperties.SetName(group, "Usage view");
            bar.Children.Add(group);
            foreach (var choice in new[] { "Token usage", "Limits" })
            {
                var button = new RadioButton { Content = choice == "Limits" ? (provider == "codex" ? "Codex Limits" : provider == "claude" ? "Claude Limits" : "Limits") : "Token Usage",
                    IsChecked = mode == choice, GroupName = "UsageMode", Style = (Style)System.Windows.Application.Current.FindResource("UsageModeButton") };
                System.Windows.Automation.AutomationProperties.SetName(button, button.Content.ToString());
                button.Checked += (_, _) => { mode = choice; Update(); }; segments.Children.Add(button);
            }
        }
        else
        {
            var periods = new[] { ("today", "Today"), ("7d", "7D"), ("30d", "30D"), ("week", "This week"), ("month", "This month"), ("all-time", "All time") };
            var select = new System.Windows.Controls.ComboBox { ItemsSource = periods.Select(x => new PeriodChoice(x.Item1, x.Item2)), DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = period, MinWidth = 145, Margin = new Thickness(0, 4, 8, 4) };
            System.Windows.Automation.AutomationProperties.SetName(select, "Usage period");
            select.SelectionChanged += (_, _) => { if (select.SelectedValue is string value) { period = value; selectedBucket = null; visibleRows = 40; Update(); } }; bar.Children.Add(select);
        }
        var refresh = Ui.AsyncButton("Refresh usage", () => store.RefreshAsync(true));
        var refreshIcon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 14 6 A 6 6 0 1 0 15 10 M 14 2 L 14 6 L 10 6"),
            Width = 16, Height = 16, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        refreshIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText");
        refresh.Content = refreshIcon; refresh.Width = refresh.Height = refresh.MinHeight = 28;
        refresh.Padding = new Thickness(6); refresh.Margin = new Thickness(16, 0, 0, 0);
        refresh.Background = Brushes.Transparent; refresh.BorderBrush = Brushes.Transparent;
        refresh.HorizontalContentAlignment = HorizontalAlignment.Center;
        refreshAction = refresh; refresh.IsEnabled = !store.IsRefreshing;
        refresh.ToolTip = "Refresh usage and limits (Ctrl+R)";
        System.Windows.Automation.AutomationProperties.SetAutomationId(refresh, "usage.refresh");
        bar.Children.Add(refresh);
        if (destination == "overview") controls.Children.Add(bar);
        else { bar.HorizontalAlignment = HorizontalAlignment.Right; filters.Children.Add(bar); }
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
        if (detailTitle is not null) detailTitle.Text = DetailTitle();
        if (refreshAction is not null) refreshAction.IsEnabled = !store.IsRefreshing;
        var view = provider + ":" + mode + ":" + destination;
        var changedView = lastView is not null && lastView != view; lastView = view;
        if (changedView)
        {
            Motion.Enter(readings);
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ScrollViewer scroll) { scroll.ScrollToTop(); break; }
        }
        accountRow.Children.Clear();
        var identity = SavedAccounts.CurrentAccountLabel(provider, store.Synthetic);
        var account = new DockPanel();
        var change = Ui.Button("Switch", () => navigate(provider is "codex" or "claude" ? provider + "-accounts" : provider));
        change.MinHeight = 20; change.Height = 20; change.Padding = new Thickness(0); change.Background = Brushes.Transparent; change.BorderThickness = new Thickness(0); change.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(change, Dock.Right); account.Children.Add(change);
        var accountLabel = Ui.Text(identity ?? store.Readings.GetValueOrDefault(provider)?.Plan ?? "Account", 12);
        accountLabel.FontWeight = FontWeights.SemiBold; accountLabel.Margin = new Thickness(0); accountLabel.TextWrapping = TextWrapping.NoWrap; accountLabel.TextTrimming = TextTrimming.CharacterEllipsis; accountLabel.ToolTip = identity; accountLabel.VerticalAlignment = VerticalAlignment.Center;
        var identityIcon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 M5,6 A3,3 0 1 0 11,6 A3,3 0 1 0 5,6 M3,13 Q8,8 13,13"), Width = 13, Height = 13, StrokeThickness = 1, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        identityIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText"); DockPanel.SetDock(identityIcon, Dock.Left); account.Children.Add(identityIcon);
        account.Children.Add(accountLabel); accountRow.Children.Add(account); account.VerticalAlignment = VerticalAlignment.Center; account.Margin = new Thickness(0, 12, 0, 12);
        readings.Children.Clear();
        if (destination != "overview") { Detail(); return; }
        if (mode == "Limits") { Limits(); return; }
        if (provider is not ("codex" or "claude"))
        {
            readings.Children.Add(Ui.Text("Local token history is available for Codex and Claude Code.", color: "#A6A6AA"));
            readings.Children.Add(Ui.Button("View provider limits", () => { mode = "Limits"; BuildControls(); Update(); })); return;
        }
        Overview();
    }
    private static DockPanel Heading(string title)
    {
        var row = new DockPanel { Margin = new Thickness(0, title == "History" ? 16 : 0, 0, 0) };
        var scope = Ui.Text("This PC", 11, "#A6A6AA"); scope.Margin = new Thickness(0); scope.ToolTip = "Local usage across accounts on this computer."; DockPanel.SetDock(scope, Dock.Right); row.Children.Add(scope);
        var heading = Ui.Text(title, 13, weight: FontWeights.SemiBold); heading.Margin = new Thickness(0); row.Children.Add(heading); return row;
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
            if ((window.Id != "rate-limit-reset-credits" || !window.RemainingCount.HasValue) && window.DisplayValue is { } value) readings.Children.Add(Ui.Text(value));
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
        var since = period switch { "today" => day, "7d" => day.AddDays(-6), "30d" => day.AddDays(-29), "week" => day.AddDays(-(((int)day.DayOfWeek + (settings.Current.WeekStart == WeekStart.Monday ? 6 : 0)) % 7)), "month" => new DateTime(day.Year, day.Month, 1), _ => DateTime.MinValue };
        return (store.Events.GetValueOrDefault(provider) ?? []).Where(x => x.OccurredAt <= now && x.OccurredAt.LocalDateTime >= since
            && (!includeSelection || (project is null || x.ProjectId == project) && (session is null || x.SessionId == session) && (selectedModel is null || x.Model == selectedModel)));
    }
    private void Detail()
    {
        var events = Filter().ToArray();
        readings.Children.Add(Ui.Text(selectedModel is not null ? selectedModel : session is not null ? "Session details" : project is not null ? "Project details" : destination switch { "projects" => "Projects", "sessions" => "Sessions", _ => "Usage history" }, 20, weight: FontWeights.SemiBold));
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
        var totals = events.Aggregate(TokenUsage.Zero, (sum, x) => sum.Add(x.Usage)); MetricSummary(readings, totals);
        var cost = UsageAnalytics.Estimate(events);
        if (settings.Current.CostEstimatesEnabled && provider == "codex")
        {
        Ui.Section(readings, cost.Label);
        readings.Children.Add(Ui.Text(cost.Amount is { } amount ? "$" + amount.ToString("N4", CultureInfo.CurrentCulture) : "Unavailable", 24));
        readings.Children.Add(Ui.Text("Estimated from bundled API pricing · not a bill", 11, "#A6A6AA"));
        if (cost.IsPartial) readings.Children.Add(Ui.Text($"Excludes {cost.ExcludedTokens:N0} tokens · " + string.Join(", ", cost.ExcludedModels), 11, "#A6A6AA"));
        }
        Timeline(events);
    }
    private sealed record PeriodChoice(string Id, string Name);
}
