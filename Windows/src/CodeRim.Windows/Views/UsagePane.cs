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
    private readonly UsageProviderPicker selector;
    private string? displayedOwner;
    private bool displayedAvailable = true;
    private (DataQuality? Quality, bool Partial) displayedLocalState;
    private (DataQuality? Quality, bool Partial) LocalState
    {
        get { var snapshot = store.Usage.GetValueOrDefault(provider); return (snapshot?.Quality, snapshot?.RetainsPartialHistory ?? false); }
    }
    private bool ProviderAvailable => provider != "claude" || store.ClaudeAvailable;
    private ProviderDefinition[] ProviderChoices() => store.AvailableUsageProviders
        .Concat(provider == "claude" && !store.ClaudeAvailable ? ["claude"] : Array.Empty<string>()).Select(id => ProviderCatalog.Find(id)!).ToArray();
    private readonly Grid header;
    private System.Windows.Controls.Button? refreshAction;
    private TextBlock? detailTitle;
    private readonly Stack<NavigationState> history = new();
    private readonly Dictionary<string, string> analyticsRanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionDetails> analyticsMetadata = new(StringComparer.Ordinal);
    private bool IsAnalyticsList => destination is "projects" or "sessions" && project is null && session is null;
    private bool searchVisible;
    private DateTimeOffset? analyticsSelectedBucket;
    private void OpenAnalytics(string target) => Forward(target, analyticsRanges.GetValueOrDefault(target, target == "projects" ? "30d" : "7d"));
    private void ChangePeriod(string value)
    {
        period = value;
        if (destination == "activity" || destination is "projects" or "sessions" && project is null && session is null)
            analyticsRanges[destination] = value;
        if (destination == "activity") analyticsSelectedBucket = null;
        selectedBucket = null; Update();
    }
    private sealed record NavigationState(string Destination, string Period, string Search, bool SearchVisible, string? Project, string? Session, string? Model, DateTimeOffset? Bucket);
    private void Forward(string target, string? selectedPeriod = null, string? selectedProject = null, string? selectedSession = null, string? model = null)
    {
        history.Push(new(destination, period, search, searchVisible, project, session, selectedModel, selectedBucket));
        destination = target; period = selectedPeriod ?? period; project = selectedProject; session = selectedSession; selectedModel = model;
        search = ""; searchVisible = false;
        selectedBucket = target == "activity" ? analyticsSelectedBucket : null;
        BuildControls(); Update();
    }
    internal void Back()
    {
        if (!history.TryPop(out var previous)) return;
        (destination, period, search, project, session, selectedModel, selectedBucket) = (previous.Destination, previous.Period, previous.Search, previous.Project, previous.Session, previous.Model, previous.Bucket);
        searchVisible = previous.SearchVisible;
        BuildControls(); Update();
    }
    internal bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (key == Key.F && modifiers == ModifierKeys.Control && IsAnalyticsList)
        {
            searchVisible = true; BuildControls();
            var field = filters.Children.OfType<TextBox>().Single();
            Dispatcher.BeginInvoke(new Action(() => { field.Focus(); field.SelectAll(); }));
            return true;
        }
        if (key == Key.OemOpenBrackets) { Back(); return true; }
        if (key is Key.D1 or Key.D2)
        {
            if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                var id = key == Key.D1 ? "codex" : "claude";
                if (store.AvailableUsageProviders.Contains(id, StringComparer.Ordinal)) selector.SelectedValue = id;
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
        if (store.AvailableUsageProviders.Contains(id, StringComparer.Ordinal)) selector.SelectedValue = id;
    }
    private string provider;
    private string period = "today";
    private string destination = "overview";
    private string mode = "Token usage";
    private string search = "";
    private string? project;
    private string? session;
    private bool pendingRefresh;
    private string? lastView;
    private string? displayedProfileAccountKey;
    private bool displayedProfileEnabled;
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
        var preferred = settings.Current.UsageProvider;
        if (provider != preferred && preferred is "codex" or "claude")
        {
            provider = preferred; destination = "overview"; project = session = null;
            period = "today"; search = ""; history.Clear(); analyticsRanges.Clear(); analyticsSelectedBucket = null; BuildControls();
        }
        RefreshProviderChoices();
        var changedAccount = displayedOwner != store.AccountDisplay(provider).OwnerKey || displayedAvailable != ProviderAvailable || displayedProfileAccountKey != store.ProfileHistory.Snapshot?.AccountKey || displayedProfileEnabled != store.ProfileHistory.Enabled;
        var changedLocalState = displayedLocalState != LocalState;
        if (!changedAccount && !changedLocalState && (readings.IsKeyboardFocusWithin || accountRow.IsKeyboardFocusWithin)) { pendingRefresh = true; return; }
        pendingRefresh = false; Update();
    }
    private void RefreshProviderChoices()
    {
        var choices = ProviderChoices(); updatingChoices = true;
        try
        {
            selector.UnavailableId = store.ClaudeAvailable ? null : "claude";
            if (!selector.Items.Cast<object>().SequenceEqual(choices)) selector.ItemsSource = choices;
            selector.SelectedValue = provider;
        }
        finally { updatingChoices = false; }
    }
    internal UsagePane(DashboardStore store, AppSettingsStore settings, string provider, Action<string?> navigate)
    {
        this.store = store; this.settings = settings; this.provider = provider is "codex" or "claude" ? provider : "codex"; this.navigate = navigate;
        var choices = ProviderChoices();
        header = new Grid { Margin = new Thickness(24, 16, 24, 6) };
        header.Children.Add(controls);
        selector = new UsageProviderPicker { UnavailableId = store.ClaudeAvailable ? null : "claude", ItemsSource = choices, ItemTemplate = ProviderTemplate(), SelectedValuePath = "Id",
            SelectedValue = this.provider, MinHeight = 34, Height = 34, Width = 142, MaxWidth = 190, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)System.Windows.Application.Current.FindResource("UsageProviderPicker") };
        TextSearch.SetTextPath(selector, "Name");
        System.Windows.Automation.AutomationProperties.SetName(selector, "Usage provider");
        selector.SelectionChanged += (_, _) =>
        {
            if (updatingChoices) return;
            if (selector.SelectedValue is string id && store.AvailableUsageProviders.Contains(id, StringComparer.Ordinal))
            {
                this.provider = id; destination = "overview"; project = session = null; search = ""; period = "today"; history.Clear(); analyticsRanges.Clear(); analyticsSelectedBucket = null;
                try { settings.Save(settings.Current with { UsageProvider = id }); }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { }
                RefreshProviderChoices(); BuildControls(); Update();
            }
            else RefreshProviderChoices(); // Reject base ComboBox keyboard/text-search selection of an unavailable item.
        };
        header.Children.Add(selector); AdaptHeader(header, controls); Children.Add(header);
        Children.Add(filters); Children.Add(accountRow); Children.Add(SettingsUi.Divider()); Children.Add(readings);
        limitClock.Tick += (_, _) => RefreshLimitClock(DateTimeOffset.Now);
        Loaded += (_, _) => { limitClock.Start(); RefreshLimitClock(DateTimeOffset.Now); };
        Unloaded += (_, _) => limitClock.Stop();
        LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (pendingRefresh && !readings.IsKeyboardFocusWithin && !accountRow.IsKeyboardFocusWithin) RefreshReadings(); }));
        BuildControls(); Update();
    }
    private string DetailTitle() => destination switch
    {
        "model" => "Model",
        "projects" => project is null ? "Projects" : "Project",
        "sessions" => session is null ? "Sessions" : "Session",
        _ => period switch { "week" => "This Week", "month" => "This Month", "all-time" => destination == "account-period" ? "Lifetime" : "Local History", "today" => "Today", _ => "Usage" }
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
    internal void ShowSessions() => OpenAnalytics("sessions");
    private void BuildControls()
    {
        controls.Children.Clear(); filters.Children.Clear(); detailTitle = null; refreshAction = null;
        filters.Margin = destination == "activity" || IsAnalyticsList ? new Thickness(16, 12, 16, 12) : new Thickness(24, 0, 24, 0);
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
        if (destination == "activity" || IsAnalyticsList)
        {
            filters.Children.Add(AnalyticsRangeControl());
            if (IsAnalyticsList && searchVisible)
            {
                var filter = new TextBox { Text = search, Padding = new Thickness(8), Margin = new Thickness(0, 10, 0, 0), ToolTip = "Find " + destination + " (Ctrl+F, Escape to close)" };
                System.Windows.Automation.AutomationProperties.SetName(filter, "Filter " + destination);
                filter.TextChanged += (_, _) => { search = filter.Text; Update(); };
                filter.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key != Key.Escape) return;
                    search = ""; searchVisible = false; BuildControls(); Update(); e.Handled = true;
                    var range = filters.Children.OfType<Border>().FirstOrDefault()?.Child as System.Windows.Controls.Primitives.UniformGrid;
                    var selected = range?.Children.OfType<RadioButton>().FirstOrDefault(x => x.IsChecked == true);
                    Dispatcher.BeginInvoke(new Action(() => selected?.Focus()));
                };
                filters.Children.Add(filter);
            }
            return;
        }
        if (destination != "overview") return;
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
        var refresh = Ui.RefreshButton("Refresh usage", () => store.RefreshAsync(true));
        refresh.Margin = new Thickness(16, 0, 0, 0);
        refreshAction = refresh; refresh.IsEnabled = !store.IsRefreshing;
        refresh.ToolTip = "Refresh usage and limits (Ctrl+R)";
        System.Windows.Automation.AutomationProperties.SetAutomationId(refresh, "usage.refresh");
        bar.Children.Add(refresh);
        controls.Children.Add(bar);
    }
    internal void Update()
    {
        displayedLocalState = LocalState;
        displayedProfileEnabled = store.ProfileHistory.Enabled;
        var profileAccountKey = store.ProfileHistory.Snapshot?.AccountKey;
        if (displayedProfileAccountKey != profileAccountKey)
        {
            foreach (var key in metricValues.Keys.Where(x => x.StartsWith("codex:account", StringComparison.Ordinal)).ToArray()) metricValues.Remove(key);
            displayedProfileAccountKey = profileAccountKey;
        }
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
        var display = store.AccountDisplay(provider);
        displayedOwner = display.OwnerKey; displayedAvailable = ProviderAvailable;
        var identity = display.Label;
        var account = new DockPanel();
        var change = Ui.Button("Switch", () => navigate(provider is "codex" or "claude" ? provider + "-accounts" : provider));
        change.MinHeight = 20; change.Height = 20; change.Padding = new Thickness(0); change.Background = Brushes.Transparent; change.BorderThickness = new Thickness(0); change.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(change, Dock.Right); account.Children.Add(change);
        var accountLabel = Ui.Text(identity ?? display.Plan ?? "Account", 12);
        accountLabel.FontWeight = FontWeights.SemiBold; accountLabel.Margin = new Thickness(0); accountLabel.TextWrapping = TextWrapping.NoWrap; accountLabel.TextTrimming = TextTrimming.CharacterEllipsis; accountLabel.ToolTip = identity; accountLabel.VerticalAlignment = VerticalAlignment.Center;
        var identityIcon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 M5,6 A3,3 0 1 0 11,6 A3,3 0 1 0 5,6 M3,13 Q8,8 13,13"), Width = 13, Height = 13, StrokeThickness = 1, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        identityIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText"); DockPanel.SetDock(identityIcon, Dock.Left); account.Children.Add(identityIcon);
        account.Children.Add(accountLabel); accountRow.Children.Add(account); account.VerticalAlignment = VerticalAlignment.Center; account.Margin = new Thickness(0, 12, 0, 12);
        readings.Children.Clear(); limitClockUpdates.Clear();
        readings.Margin = IsAnalyticsList ? new Thickness(0)
            : mode == "Limits" && destination == "overview" || destination is "activity" or "model" or "projects" or "sessions" ? new Thickness(16) : new Thickness(24, 16, 24, 16);
        if (!ProviderAvailable)
        {
            accountRow.Children.Clear();
            readings.Children.Add(Ui.Text(store.Claude.Message, color: "#A6A6AA"));
            readings.Children.Add(Ui.Button("Open Claude Settings", () => navigate("claude"))); return;
        }
        if (destination is "account-period" or "local-period") { PeriodDetail(); return; }
        if (destination != "overview") { Detail(); return; }
        if (mode == "Limits") { limitClockOwnerKey = display.OwnerKey; Limits(display.Reading); return; }
        if (provider is not ("codex" or "claude"))
        {
            readings.Children.Add(Ui.Text("Local token history is available for Codex and Claude Code.", color: "#A6A6AA"));
            readings.Children.Add(Ui.Button("View provider limits", () => { mode = "Limits"; BuildControls(); Update(); })); return;
        }
        Overview();
    }
    private static DockPanel Heading(string title, string scopeTitle = "This PC")
    {
        var row = new DockPanel { Margin = new Thickness(0, title == "History" ? 16 : 0, 0, 0) };
        var scope = Ui.Text(scopeTitle, 11, "#A6A6AA"); scope.Margin = new Thickness(0); scope.ToolTip = scopeTitle == "This PC" ? "Local usage across accounts on this computer." : AccountHistoryHelp; DockPanel.SetDock(scope, Dock.Right); row.Children.Add(scope);
        var heading = Ui.Text(title, 13, weight: FontWeights.SemiBold); heading.Margin = new Thickness(0); row.Children.Add(heading); return row;
    }
    private void Limits(ProviderReading? accountReading)
    {
        if (provider is "codex" or "claude") { AccountLimits(accountReading); return; }
        var reading = ProviderDisplayPolicy.Apply(accountReading?.Evaluated(DateTimeOffset.Now), settings.Current);
        readings.Children.Add(Ui.Text(reading?.Plan ?? ProviderCatalog.Find(provider)?.Name ?? provider, 18, weight: FontWeights.SemiBold));
        foreach (var window in reading?.Windows ?? [])
        {
            if (provider == "codex" && window.Id == ProviderDisplayPolicy.ResetCreditsId)
            {
                readings.Children.Add(ResetCreditsCard(window)); continue;
            }
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
    private static Border ResetCreditsCard(LimitWindow credits)
    {
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(12) };
        var value = credits.DisplayValue == "Unlimited resets" ? "Unlimited"
            : credits.RemainingCount is >= 0 ? credits.RemainingCount.Value.ToString("N0", CultureInfo.CurrentCulture) : "Available";
        if (credits.ResetsAt is { } expiration)
        {
            var text = Ui.Text("· " + (CalendarDateText.MonthDay(expiration.LocalDateTime) ?? "Date unavailable"), 12, "#A6A6AA");
            text.Margin = new Thickness(8, 0, 0, 0); text.VerticalAlignment = VerticalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetAutomationId(text, "usage.reset-credits.expiration");
            DockPanel.SetDock(text, Dock.Right); row.Children.Add(text);
        }
        var count = Ui.Text(value); count.Margin = new Thickness(8, 0, 0, 0); count.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Documents.Typography.SetNumeralAlignment(count, FontNumeralAlignment.Tabular);
        System.Windows.Automation.AutomationProperties.SetAutomationId(count, "usage.reset-credits.value");
        DockPanel.SetDock(count, Dock.Right); row.Children.Add(count);
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M12,3 A6,6 0 1 1 3,4 M3,1 V4 H6"), Width = 15, Height = 15,
            Stretch = Stretch.Uniform, StrokeThickness = 1.2, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText"); DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        var label = Ui.Text("Reset credits"); label.Margin = new Thickness(0); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
        var card = new Border { Child = row, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 16, 0, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "LimitCardBackground");
        System.Windows.Automation.AutomationProperties.SetAutomationId(card, "usage.reset-credits");
        System.Windows.Automation.AutomationProperties.SetName(card, "Reset credits, " + value);
        return card;
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
        if (!AnalyticsReady()) return;
        analyticsMetadata.Clear();
        if (destination is "projects" or "sessions")
            foreach (var item in store.SessionDetails.GetValueOrDefault(provider) ?? []) analyticsMetadata[item.Id] = item;
        var events = Filter().ToArray();
        if (destination == "activity")
        {
            var total = events.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage));
            var quality = AnalyticsQuality(total);
            var estimate = quality == DataQuality.Unavailable ? new CostSummary(null, 0, []) : UsageAnalytics.Estimate(events);
            AnalyticsSummary(readings, total, estimate, compact: false, quality); Timeline(events, estimate, quality); return;
        }
        if (destination == "model" && selectedModel is not null)
        {
            var name = Ui.Text(selectedModel, 13, weight: FontWeights.SemiBold); name.Margin = new Thickness(0, 0, 0, 14); readings.Children.Add(name);
            if (events.Length == 0) { readings.Children.Add(Ui.Text("No local usage observed for this period.", color: "#A6A6AA")); return; }
            var total = events.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage));
            var quality = AnalyticsQuality(Filter(includeSelection: false).Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage)));
            var estimate = quality == DataQuality.Unavailable ? new CostSummary(null, 0, []) : UsageAnalytics.Estimate(events);
            AnalyticsSummary(readings, total, estimate, compact: false, quality);
            var breakdown = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; AnalyticsBreakdown(breakdown, total); readings.Children.Add(breakdown);
            readings.Children.Add(AnalyticsValueRow("Projects", events.Select(x => x.ProjectId).Distinct(StringComparer.Ordinal).LongCount(), 14));
            readings.Children.Add(AnalyticsValueRow("Sessions", events.Select(x => x.SessionId).Distinct(StringComparer.Ordinal).LongCount(), 14));
            return;
        }
        if (destination is "projects" or "sessions" && project is null && session is null)
        {
            if (events.Length == 0)
            {
                var empty = Ui.Text(destination == "projects" ? "No project-tagged usage in this range." : "No sessions in this range.", 11, "#A6A6AA");
                empty.Margin = new Thickness(24); readings.Children.Add(empty); return;
            }
            var quality = AnalyticsQuality(events.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage)));
            var visibleSessions = events.Select(x => x.SessionId).ToHashSet(StringComparer.Ordinal);
            var childCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (destination == "sessions" && settings.Current.AgentDetailsEnabled)
                foreach (var metadata in analyticsMetadata.Values)
                    if (metadata.ParentId is { } parent && visibleSessions.Contains(metadata.Id))
                        childCounts[parent] = childCounts.GetValueOrDefault(parent) + 1;
            var rows = events.GroupBy(x => destination == "projects" ? x.ProjectId : x.SessionId).Select(g =>
            {
                return new { Id = g.Key, Name = AnalyticsEntityName(g, g.Key, destination == "sessions"),
                    Total = g.Aggregate(TokenUsage.Zero, (sum, x) => sum.Add(x.Usage)).TotalTokens, LastActivity = g.Max(x => x.OccurredAt),
                    Sessions = g.Select(x => x.SessionId).Distinct(StringComparer.Ordinal).LongCount(), Cost = UsageAnalytics.Estimate(g) };
            }).Where(g => g.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || g.Id.Contains(search, StringComparison.OrdinalIgnoreCase));
            var groups = (destination == "projects"
                ? rows.OrderByDescending(g => g.Total).ThenBy(g => g.Name, StringComparer.Ordinal).ThenBy(g => g.Id, StringComparer.Ordinal)
                : rows.OrderByDescending(g => g.LastActivity).ThenBy(g => g.Id, StringComparer.Ordinal)).ToArray();
            if (groups.Length == 0)
            {
                var empty = Ui.Text("No matching " + destination + ".", 11, "#A6A6AA"); empty.Margin = new Thickness(24); readings.Children.Add(empty);
            }
            foreach (var group in groups)
            {
                var detail = destination == "projects" ? group.Sessions.ToString("N0", CultureInfo.CurrentCulture) + (group.Sessions == 1 ? " session" : " sessions")
                    : SessionListDetail(group.Id, group.LastActivity, childCounts);
                var costText = settings.Current.CostEstimatesEnabled && provider == "codex" && quality != DataQuality.Unavailable && group.Cost.Amount is { } amount
                    ? "~" + AnalyticsCurrency(amount) + (group.Cost.IsPartial ? " · subtotal" : "") : "";
                readings.Children.Add(AnalyticsRowButton(group.Name, detail, group.Total, costText,
                    "usage." + (destination == "projects" ? "project." : "session.") + group.Id, 16,
                    () => Forward(destination, selectedProject: destination == "projects" ? group.Id : null, selectedSession: destination == "sessions" ? group.Id : null)));
            }
            return;
        }
        if (project is not null || session is not null) EntityDetail(events);
    }
    private string AnalyticsEntityName(IEnumerable<UsageEvent> events, string id, bool isSession)
    {
        if (isSession && provider == "codex" && store.CodexAnalyticsLabels.GetValueOrDefault(id)?.SessionTitle is { } title) return title;
        if (isSession && analyticsMetadata.GetValueOrDefault(id)?.ProjectName is { Length: > 0 } name)
            return name;
        // A session can acquire metadata after its first event. Import order
        // must not choose the name in either the list or its detail.
        var named = events.OrderByDescending(HasProjectName).ThenByDescending(x => x.OccurredAt)
            .ThenBy(x => x.EventKey, StringComparer.Ordinal).ThenBy(x => x.ProjectId, StringComparer.Ordinal)
            .ThenBy(x => x.Project, StringComparer.Ordinal).First();
        if (!isSession && provider == "codex" && named.Project.ToLowerInvariant() is "" or "/" or "codex" or ".codex" or "unknown project")
        {
            var names = events.Select(x => store.CodexAnalyticsLabels.GetValueOrDefault(x.SessionId)?.ProjectName).OfType<string>()
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (names.Length > 0) return string.Join(" · ", names.Take(2)) + (names.Length > 2 ? " (+" + (names.Length - 2).ToString(CultureInfo.CurrentCulture) + ")" : "");
        }
        return !isSession || HasProjectName(named) ? named.Project : "Session " + id[..Math.Min(8, id.Length)];
    }
    private static bool HasProjectName(UsageEvent item) => !string.IsNullOrWhiteSpace(item.Project)
        && (item.Project != "Unknown project" || item.ProjectId != "unknown");
    private string SessionListDetail(string id, DateTimeOffset lastActivity, IReadOnlyDictionary<string, int> childCounts)
    {
        var parts = new List<string> { AnalyticsDateText.Format(lastActivity, AnalyticsDateStyle.DayAndTime) };
        if (settings.Current.AgentDetailsEnabled)
        {
            var count = childCounts.GetValueOrDefault(id);
            if (count > 0) parts.Add(count.ToString("N0", CultureInfo.CurrentCulture) + (count == 1 ? " agent" : " agents"));
        }
        if (settings.Current.AttachmentMetadataEnabled)
        {
            var count = analyticsMetadata.GetValueOrDefault(id)?.Attachments.Sum(x => (long)x.Count) ?? 0;
            if (count > 0) parts.Add(count.ToString("N0", CultureInfo.CurrentCulture) + (count == 1 ? " whole-session image" : " whole-session images"));
        }
        return string.Join(" · ", parts);
    }
}
