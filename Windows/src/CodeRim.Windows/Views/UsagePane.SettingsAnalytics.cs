using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class UsagePane
{
    private readonly Dictionary<string, AnalyticsSnapshotCache> analyticsSources = new(StringComparer.Ordinal);
    private AnalyticsSourceFrame? detailAnalyticsFrame;
    private SettingsUsageGrouping settingsGrouping;
    private DateTimeOffset? settingsSelectedDate;
    private bool showsAllSettingsSessions;
    private readonly HashSet<string> expandedSettingsSessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> expandedSettingsPeriods = new(StringComparer.Ordinal);
    private string SettingsRangeKey => analyticsRanges.GetValueOrDefault("activity", "7d") == "30d" ? "30d" : "7d";
    private string? FocusedAnalyticsControlId()
    {
        for (DependencyObject? control = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
             control is not null && !ReferenceEquals(control, readings); control = VisualTreeHelper.GetParent(control))
            if (control is FrameworkElement element && AutomationProperties.GetAutomationId(element) is { Length: > 0 } id
                && id.StartsWith("settings.usage.", StringComparison.Ordinal)) return id;
        return null;
    }
    private void RestoreAnalyticsControlFocus(string id)
    {
        FrameworkElement? Find(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement element && AutomationProperties.GetAutomationId(element) == id) return element;
                if (Find(child) is { } found) return found;
            }
            return null;
        }
        Find(readings)?.Focus();
    }
    private void ResetSettingsAnalytics()
    {
        requestedSettingsSection = SettingsUsageSection.Analytics; settingsGrouping = SettingsUsageGrouping.TokenType;
        settingsSelectedDate = null; showsAllSettingsSessions = false; expandedSettingsSessions.Clear(); expandedSettingsPeriods.Clear();
        analyticsSources.Clear(); detailAnalyticsFrame = null;
    }
    private AnalyticsSourceFrame? ReadAnalyticsFrame(string key)
    {
        if (!analyticsSources.TryGetValue(provider, out var cache)) analyticsSources[provider] = cache = new();
        return cache.Read(key, store.AnalyticsSources.GetValueOrDefault(provider), store.Usage.GetValueOrDefault(provider)?.Quality,
            store.LocalAnalyticsEpochs.GetValueOrDefault(provider), store.IsLocalReading || store.RebuildingProviders.Contains(provider));
    }
    private void ResetSettingsRangeSelection()
    {
        settingsSelectedDate = null; showsAllSettingsSessions = false; expandedSettingsSessions.Clear();
    }
    private void SetSettingsRange(string value)
    {
        if (SettingsRangeKey != value) ResetSettingsRangeSelection();
        analyticsRanges["activity"] = value; Update();
    }
    private void SettingsAnalytics()
    {
        var root = new StackPanel { MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(root, "settings.usage.analytics"); readings.Children.Add(root);
        var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = SettingsHeading("Total usage", "Token activity on this PC"); top.Children.Add(title);
        var chartControls = SettingsChartControls(); Grid.SetColumn(chartControls, 1); top.Children.Add(chartControls);
        bool? compact = null;
        top.SizeChanged += (_, _) =>
        {
            var next = top.ActualWidth < 540; if (compact == next) return; compact = next;
            top.ColumnDefinitions.Clear(); top.RowDefinitions.Clear(); top.ColumnDefinitions.Add(new ColumnDefinition());
            top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (next) top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            else top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(chartControls, next ? 1 : 0); Grid.SetColumn(chartControls, next ? 0 : 1);
            chartControls.Margin = next ? new Thickness(0, 12, 0, 0) : new Thickness(16, 0, 0, 0);
        };
        root.Children.Add(top);
        var frame = ReadAnalyticsFrame(SettingsRangeKey);
        var snapshot = frame is null ? null : SettingsUsageAnalytics.Build(frame,
            SettingsRangeKey == "30d" ? AnalyticsRange.ThirtyDays : AnalyticsRange.SevenDays, TimeZoneInfo.Local);
        if (snapshot is null || snapshot.Quality is DataQuality.Unavailable or DataQuality.Error)
        {
            var loading = store.IsLocalReading || store.RebuildingProviders.Contains(provider) || !store.Usage.ContainsKey(provider);
            var placeholder = new Grid { MinHeight = 180 };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (loading) row.Children.Add(new LimitActivityIndicator { Margin = new Thickness(0, 0, 10, 0) });
            row.Children.Add(Ui.Text(loading ? "Reading usage…" : "Usage unavailable", 13, "#A6A6AA")); placeholder.Children.Add(row);
            AutomationProperties.SetAutomationId(placeholder, "settings.usage." + (loading ? "loading" : "unavailable"));
            root.Children.Add(SettingsAnalyticsCard(placeholder)); return;
        }
        analyticsMetadata.Clear();
        foreach (var item in store.SessionDetails.GetValueOrDefault(provider) ?? []) analyticsMetadata[item.Id] = item;
        var card = new StackPanel { Margin = new Thickness(18) };
        var metricRow = new DockPanel();
        var updated = Ui.Text(AnalyticsDateText.Format(snapshot.Through, AnalyticsDateStyle.DayAndTime), 11, "#A6A6AA");
        updated.Margin = new Thickness(12, 0, 0, 0); updated.VerticalAlignment = VerticalAlignment.Center;
        updated.ToolTip = "Updated " + snapshot.Through.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
        DockPanel.SetDock(updated, Dock.Right); metricRow.Children.Add(updated);
        metricRow.Children.Add(SettingsMetric("settings-total", snapshot.Usage.TotalTokens, "Tokens", "settings.usage.total")); card.Children.Add(metricRow);
        if (snapshot.Usage.IsZero)
        {
            var empty = Ui.Text("No usage in this period", 13, "#A6A6AA"); empty.MinHeight = 180;
            AutomationProperties.SetAutomationId(empty, "settings.usage.empty"); card.Children.Add(empty);
        }
        else
        {
            card.Children.Add(SettingsUsageChart(snapshot, settingsGrouping, line: false));
            var caption = new DockPanel { Margin = new Thickness(0, 18, 0, 18) };
            var shares = Ui.Text("Share of tokens", 11, "#A6A6AA"); shares.Margin = new Thickness(12, 0, 0, 0); DockPanel.SetDock(shares, Dock.Right); caption.Children.Add(shares);
            var selected = SettingsUsageAnalytics.DayAt(snapshot, settingsSelectedDate, TimeZoneInfo.Local);
            if (settingsSelectedDate is not null)
            {
                var clear = HistoryLink("Clear selection", () => { settingsSelectedDate = null; Update(); });
                AutomationProperties.SetAutomationId(clear, "settings.usage.clearSelection"); clear.Margin = new Thickness(12, 0, 0, 0); DockPanel.SetDock(clear, Dock.Right); caption.Children.Add(clear);
            }
            var label = Ui.Text(selected is null ? (snapshot.Range == AnalyticsRange.SevenDays ? "7" : "30") + "-day total"
                : AnalyticsDateText.Format(selected.Start, AnalyticsDateStyle.Day), 11, "#A6A6AA"); label.Margin = new Thickness(0);
            if (selected is not null) AutomationProperties.SetAutomationId(label, "settings.usage.selectedDate"); caption.Children.Add(label); card.Children.Add(caption);
            card.Children.Add(SettingsLegend(SettingsUsageAnalytics.Series(snapshot, settingsGrouping, settings.Current.ShowCachedInput, selected), modelActivity: false));
        }
        if (store.Usage.GetValueOrDefault(provider)?.Quality is DataQuality.Stale or DataQuality.Error)
            card.Children.Add(SettingsNotice("Showing the last analytics snapshot", "settings.usage.stale"));
        else if (snapshot.Quality == DataQuality.Partial)
            card.Children.Add(SettingsNotice("Some usage could not be read. Totals may be incomplete.", "settings.usage.partial"));
        root.Children.Add(SettingsAnalyticsCard(card));
        if (settings.Current.SessionsEnabled) SettingsTopSessions(root, frame!, snapshot);
        SettingsLocalHistory(root, frame!.Local);
        root.Children.Add(SettingsHeading("Model activity", "Daily token usage by model · This PC", section: true));
        var models = new StackPanel { Margin = new Thickness(18) };
        models.Children.Add(SettingsMetric("settings-model-count", snapshot.Models.Count, "Models used", "settings.usage.modelCount"));
        models.Children.Add(SettingsUsageChart(snapshot, SettingsUsageGrouping.Model, line: true));
        var legend = SettingsLegend(SettingsUsageAnalytics.Series(snapshot, SettingsUsageGrouping.Model, settings.Current.ShowCachedInput), modelActivity: true);
        legend.Margin = new Thickness(0, 18, 0, 0); models.Children.Add(legend);
        var modelCard = SettingsAnalyticsCard(models); AutomationProperties.SetAutomationId(modelCard, "settings.usage.modelActivity"); root.Children.Add(modelCard);
    }
    private StackPanel SettingsChartControls()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        var range = new UniformGrid { Columns = 2 };
        var group = new Border { Child = range, Width = 128, Height = 28, Padding = new Thickness(2), CornerRadius = new CornerRadius(6) };
        group.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); AutomationProperties.SetAutomationId(group, "settings.usage.range");
        foreach (var (key, title) in new[] { ("7d", "7 days"), ("30d", "30 days") })
        {
            var choice = new RadioButton { Content = title, IsChecked = SettingsRangeKey == key, GroupName = "SettingsUsageRange", Style = (Style)FindResource("UsageModeButton") };
            AutomationProperties.SetAutomationId(choice, "settings.usage.range." + key); AutomationProperties.SetName(choice, title);
            choice.Checked += (_, _) => SetSettingsRange(key); range.Children.Add(choice);
        }
        row.Children.Add(group);
        var grouping = new ComboBox { ItemsSource = new[] { "By token type", "By model" }, SelectedIndex = settingsGrouping == SettingsUsageGrouping.TokenType ? 0 : 1,
            Width = 148, Height = 28, Margin = new Thickness(8, 0, 0, 0), FontSize = 12 };
        AutomationProperties.SetAutomationId(grouping, "settings.usage.grouping"); AutomationProperties.SetName(grouping, "Group usage");
        grouping.SelectionChanged += (_, _) => { settingsGrouping = grouping.SelectedIndex == 0 ? SettingsUsageGrouping.TokenType : SettingsUsageGrouping.Model; Update(); };
        row.Children.Add(grouping); return row;
    }
    private StackPanel SettingsMetric(string key, long count, string label, string id)
    {
        var column = new StackPanel(); var caption = Ui.Text(label, 11, "#A6A6AA"); caption.Margin = new Thickness(0, 0, 0, 4); column.Children.Add(caption);
        var number = Metric(key, count, 28); number.FontWeight = FontWeights.SemiBold; number.Margin = new Thickness(0);
        number.TextWrapping = TextWrapping.NoWrap;
        column.Children.Add(new Viewbox { Child = number, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left, MaxHeight = 38 });
        column.SizeChanged += (_, _) => number.MaxWidth = Math.Max(1, column.ActualWidth / .65);
        AutomationProperties.SetAutomationId(column, id);
        AutomationProperties.SetName(column, label + ", " + count.ToString("N0", CultureInfo.CurrentCulture)); return column;
    }
    private static StackPanel SettingsHeading(string title, string subtitle, bool section = false)
    {
        var heading = new StackPanel { Margin = new Thickness(0, section ? 28 : 0, 0, 0) };
        var name = Ui.Text(title, 13, weight: FontWeights.SemiBold); name.Margin = new Thickness(0, 0, 0, 6); heading.Children.Add(name);
        AutomationProperties.SetHeadingLevel(name, AutomationHeadingLevel.Level2);
        var detail = Ui.Text(subtitle, 13, "#A6A6AA"); detail.Margin = new Thickness(0); heading.Children.Add(detail); return heading;
    }
    private static Border SettingsAnalyticsCard(FrameworkElement content)
    {
        var border = new Border { Child = content, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(0, 12, 0, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "LimitCardBackground"); border.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); return border;
    }
    private static TextBlock SettingsNotice(string text, string id)
    {
        var notice = Ui.Text(text, 11, "#A6A6AA"); notice.Margin = new Thickness(0, 18, 0, 0); AutomationProperties.SetAutomationId(notice, id); return notice;
    }
    private Grid SettingsLegend(IReadOnlyList<SettingsUsageSeries> series, bool modelActivity)
    {
        var grid = new Grid(); for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        AutomationProperties.SetAutomationId(grid, modelActivity ? "settings.usage.modelLegend" : "settings.usage.legend");
        var total = series.Sum(s => (double)s.Tokens);
        for (var i = 0; i < series.Count; i++)
        {
            if (i % 3 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var item = series[i]; var row = new DockPanel { Margin = new Thickness(0, i >= 3 ? 16 : 0, 16, 0) };
            var color = new Border { Width = 3, Height = 34, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 10, 0), Background = SettingsSeriesColor(i) };
            DockPanel.SetDock(color, Dock.Left); row.Children.Add(color);
            var labels = new StackPanel(); var title = Ui.Text(item.Title, 11, "#A6A6AA"); title.Margin = new Thickness(0, 0, 0, 5);
            title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = item.Title; labels.Children.Add(title);
            labels.Children.Add(Ui.Text(total > 0 ? ((double)item.Tokens / total).ToString("P1", CultureInfo.CurrentCulture) : "—")); row.Children.Add(labels);
            AutomationProperties.SetAutomationId(row, (modelActivity ? "settings.usage.modelSeries." : "settings.usage.series.") + item.Id);
            AutomationProperties.SetName(row, item.Title + ", " + item.Tokens.ToString("N0", CultureInfo.CurrentCulture) + " tokens, "
                + (total > 0 ? ((double)item.Tokens / total).ToString("P1", CultureInfo.CurrentCulture) : "no usage")); row.ToolTip = item.Tokens.ToString("N0", CultureInfo.CurrentCulture) + " tokens";
            Grid.SetColumn(row, i % 3); Grid.SetRow(row, i / 3); grid.Children.Add(row);
        }
        return grid;
    }
    private void SettingsTopSessions(Panel root, AnalyticsSourceFrame frame, SettingsUsageSnapshot snapshot)
    {
        root.Children.Add(SettingsHeading("Top sessions", "Sessions ranked by tokens in this period", section: true));
        var rows = new StackPanel(); AutomationProperties.SetAutomationId(rows, "settings.usage.topSessions");
        rows.Children.Add(SettingsSessionHeader("Session", "Share", "Tokens", secondary: true));
        foreach (var item in snapshot.Sessions.Take(showsAllSettingsSessions ? snapshot.Sessions.Count : 5))
        {
            rows.Children.Add(SettingsUi.Divider());
            var name = AnalyticsEntityName(frame.Events.Where(e => e.SessionId == item.Id), item.Id, isSession: true);
            var disclosure = SettingsDisclosure("settings.usage.session." + item.Id,
                SettingsSessionHeader(name, ((double)item.Usage.TotalTokens / snapshot.Usage.TotalTokens).ToString("P1", CultureInfo.CurrentCulture),
                    TokenFormatter.Format(item.Usage.TotalTokens, settings.Current.NumberStyle)), expandedSettingsSessions.Contains(item.Id));
            disclosure.Expanded += (_, _) => expandedSettingsSessions.Add(item.Id); disclosure.Collapsed += (_, _) => expandedSettingsSessions.Remove(item.Id);
            var detail = new StackPanel(); var id = Ui.Text(item.Id, 11, "#A6A6AA"); id.FontFamily = new FontFamily("Consolas"); detail.Children.Add(id);
            SettingsTokenBreakdown(detail, item.Usage);
            detail.Children.Add(Ui.Text("Last active " + AnalyticsDateText.Format(item.LastActivityAt, AnalyticsDateStyle.DayAndTime), 11, "#A6A6AA"));
            var link = HistoryLink("Session details ↗", () => Forward("sessions", SettingsRangeKey, selectedSession: item.Id));
            AutomationProperties.SetAutomationId(link, "settings.usage.sessionDetails." + item.Id); detail.Children.Add(link); disclosure.Content = detail; rows.Children.Add(disclosure);
        }
        root.Children.Add(SettingsAnalyticsCard(rows));
        if (snapshot.Sessions.Count > 5)
        {
            var more = Ui.Button(showsAllSettingsSessions ? "Show less" : "Show more", () => { showsAllSettingsSessions = !showsAllSettingsSessions; Update(); });
            more.Margin = new Thickness(0, 12, 0, 0); more.HorizontalAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetAutomationId(more, "settings.usage.moreSessions"); root.Children.Add(more);
        }
    }
    private static Grid SettingsSessionHeader(string name, string share, string tokens, bool secondary = false)
    {
        var row = new Grid { Margin = new Thickness(16, 10, 16, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        var labels = new[] { name, share, tokens };
        for (var i = 0; i < labels.Length; i++)
        {
            var text = Ui.Text(labels[i], secondary ? 11 : 13, secondary ? "#A6A6AA" : "#EEEEF0"); text.Margin = new Thickness(0);
            text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis; text.ToolTip = labels[i];
            if (i > 0) { text.TextAlignment = TextAlignment.Right; System.Windows.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular); }
            Grid.SetColumn(text, i); row.Children.Add(text);
        }
        return row;
    }
    private void SettingsLocalHistory(Panel root, UsageSnapshot local)
    {
        root.Children.Add(SettingsHeading("Token history", "This PC · calendar periods", section: true));
        var rows = new StackPanel(); AutomationProperties.SetAutomationId(rows, "settings.usage.history");
        foreach (var (key, title, usage) in new[] { ("today", "Today", local.Today), ("week", "This Week", local.Week), ("month", "This Month", local.Month), ("all-time", "Local History", local.AllTime) })
        {
            if (rows.Children.Count > 0) rows.Children.Add(SettingsUi.Divider());
            var header = EntityTextRow(title, local.UpdatedAt is null ? "—" : TokenFormatter.Format(usage.TotalTokens, settings.Current.NumberStyle)); header.Margin = new Thickness(16, 12, 16, 12);
            var disclosure = SettingsDisclosure("settings.usage.period." + key, header, expandedSettingsPeriods.Contains(key)); disclosure.IsEnabled = local.UpdatedAt is not null;
            disclosure.Expanded += (_, _) => expandedSettingsPeriods.Add(key); disclosure.Collapsed += (_, _) => expandedSettingsPeriods.Remove(key);
            var details = new StackPanel(); SettingsTokenBreakdown(details, usage);
            var link = HistoryLink("View details ↗", () => Forward("local-period", key)); AutomationProperties.SetAutomationId(link, "settings.usage.periodDetails." + key);
            details.Children.Add(link); disclosure.Content = details; rows.Children.Add(disclosure);
        }
        root.Children.Add(SettingsAnalyticsCard(rows));
    }
    private void SettingsTokenBreakdown(Panel panel, TokenUsage usage)
    {
        var items = new List<(string, long)> { ("Input", usage.InputTokens) };
        if (settings.Current.ShowCachedInput) items.Add(("Cached input · included in Input", usage.CachedInputTokens));
        items.Add(("Output", usage.OutputTokens));
        foreach (var (title, tokens) in items)
        { var row = EntityTextRow(title, TokenFormatter.Format(tokens, settings.Current.NumberStyle)); row.Margin = new Thickness(0, 0, 0, 8); panel.Children.Add(row); }
    }
    private static Expander SettingsDisclosure(string id, FrameworkElement header, bool expanded)
    {
        var disclosure = new Expander { Header = header, IsExpanded = expanded, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(disclosure, id); disclosure.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
        var root = new FrameworkElementFactory(typeof(StackPanel));
        var toggle = new FrameworkElementFactory(typeof(ToggleButton));
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsExpanded") { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay });
        toggle.SetBinding(ContentControl.ContentProperty, new Binding("Header") { RelativeSource = RelativeSource.TemplatedParent });
        toggle.SetValue(Control.BackgroundProperty, Brushes.Transparent); toggle.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        toggle.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch);
        toggle.SetValue(Control.TemplateProperty, SettingsDisclosureToggle()); root.AppendChild(toggle);
        var detail = new FrameworkElementFactory(typeof(ContentPresenter), "Details");
        detail.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
        detail.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed); detail.SetValue(FrameworkElement.MarginProperty, new Thickness(32, 0, 16, 12)); root.AppendChild(detail);
        var template = new ControlTemplate(typeof(Expander)) { VisualTree = root };
        var open = new Trigger { Property = Expander.IsExpandedProperty, Value = true }; open.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Details")); template.Triggers.Add(open);
        disclosure.Template = template; return disclosure;
    }
    private static ControlTemplate SettingsDisclosureToggle()
    {
        var root = new FrameworkElementFactory(typeof(DockPanel)); root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var arrow = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path), "Chevron");
        arrow.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M1,1 L5,5 L1,9")); arrow.SetValue(FrameworkElement.WidthProperty, 6d); arrow.SetValue(FrameworkElement.HeightProperty, 10d);
        arrow.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText"); arrow.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.2);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(16, 0, 0, 0)); arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(UIElement.RenderTransformOriginProperty, new Point(.5, .5)); arrow.SetValue(DockPanel.DockProperty, Dock.Left); root.AppendChild(arrow);
        root.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = root };
        var selected = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true }; selected.Setters.Add(new Setter(UIElement.RenderTransformProperty, new RotateTransform(90), "Chevron")); template.Triggers.Add(selected);
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true }; focused.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("AccentBrush"))); template.Triggers.Add(focused);
        return template;
    }
}
