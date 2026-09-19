using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;

namespace CodeRim.Windows.Views;

internal sealed partial class DashboardWindow : Window
{
    private static readonly string[] PercentageOptions = ["Used", "Remaining"];
    private static readonly string[] ControlOptions = ["Auto", "Start", "End"];
    private static readonly string[] ResetOptions = ["Relative", "Absolute"];
    private static readonly int[] RefreshOptions = new[] { 0, 30, 60, 300 };
    private static readonly double[] ScaleOptions = new[] { 0.8, 1.0, 1.25 };
    private static readonly string[] AccentOptions = new[] { "#00FF88", "#3B9CFF", "#9B7DFF", "#FF6EC7", "#FF9F3F" };
    private static readonly string[] GradientOptions = new[] { "Aurora", "Ocean", "Sunset", "Spectrum" };
    private readonly DashboardStore store;
    private readonly AppSettingsStore settings;
    private readonly CredentialVault vault;
    private readonly ListBox sidebar = new() { BorderThickness = new Thickness(0), Padding = new Thickness(10, 10, 10, 0) };
    private readonly StackPanel body = new() { Margin = new Thickness(0, 6, 0, 28) };
    private readonly Dictionary<string, Window> accountWindows = new(StringComparer.Ordinal);
    private readonly TextBlock status = Ui.Text("");
    private readonly StackPanel providerReading = new();
    private string page = "usage";
    private string localProvider = "codex";
    private bool refreshingSidebar;
    public DashboardWindow(DashboardStore store, AppSettingsStore settings, CredentialVault vault)
    {
        this.store = store; this.settings = settings; this.vault = vault; localProvider = settings.Current.UsageProvider;
        Title = "CodeRim"; Width = 980; Height = 680; MinWidth = 840; MinHeight = 560;
        SetResourceReference(BackgroundProperty, "WindowBackground"); SetResourceReference(ForegroundProperty, "PrimaryText");
        sidebar.SetResourceReference(Control.BackgroundProperty, "PanelBackground"); FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(216), MinWidth = 200, MaxWidth = 260 }); layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.Children.Add(sidebar);
        layout.Children.Add(new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.CurrentAndNext });
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1); layout.Children.Add(scroll); Content = layout;
        sidebar.SelectionChanged += (_, _) => { if (!refreshingSidebar && sidebar.SelectedItem is ListBoxItem item && item.Tag is string id) Navigate(id); };
        settings.SettingsChanged += SettingsChanged; store.PropertyChanged += StoreChanged;
        Closed += (_, _) => { settings.SettingsChanged -= SettingsChanged; store.PropertyChanged -= StoreChanged; };
        PreviewKeyDown += (_, e) =>
        {
            if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
            {
                if (e.Key == System.Windows.Input.Key.OemComma) { Navigate("general"); e.Handled = true; }
                else if (e.Key == System.Windows.Input.Key.R) { _ = store.RefreshAsync(true); e.Handled = true; }
                else if (page == "usage" && usagePane is not null) e.Handled = usagePane.HandleShortcut(e.Key, System.Windows.Input.Keyboard.Modifiers);
            }
        };
        BuildSidebar(); Navigate("usage");
    }
    public void Navigate(string? id)
    {
        if (id?.StartsWith("sessions:", StringComparison.Ordinal) == true)
        {
            localProvider = id[9..]; page = "usage"; Render(); usagePane?.SelectProvider(localProvider); usagePane?.ShowSessions(); Show(); Activate(); return;
        }
        if (id is "codex-accounts" or "claude-accounts") { OpenAccounts(id.Split('-')[0]); return; }
        page = id ?? "general";
        if (ProviderCatalog.Find(page) is { } provider && !settings.Current.EnabledProviders.Contains(provider.Id, StringComparer.Ordinal)) page = "providers";
        refreshingSidebar = true;
        sidebar.SelectedItem = sidebar.Items.OfType<ListBoxItem>().FirstOrDefault(x => Equals(x.Tag, ProviderCatalog.Find(page) is not null ? "providers" : page));
        refreshingSidebar = false;
        Render(); Show(); Activate();
    }
    private void BuildSidebar()
    {
        refreshingSidebar = true; sidebar.Items.Clear();
        foreach (var (id, title) in new[] { ("general", "General"), ("usage", "Usage"), ("providers", "Providers"), ("notch", "Notch"), ("diagnostics", "Diagnostics"), ("about", "Information") })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(SettingsUi.Icon(id));
            var text = Ui.Text(title); text.Margin = new Thickness(8, 0, 0, 0); text.VerticalAlignment = VerticalAlignment.Center;
            text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Control.Foreground))
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) });
            row.Children.Add(text);
            var item = new ListBoxItem { Content = row, Tag = id, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 2, 0, 2) };
            System.Windows.Automation.AutomationProperties.SetName(item, title);
            sidebar.Items.Add(item);
        }
        System.Windows.Automation.AutomationProperties.SetName(sidebar, "Settings sections");
        sidebar.SelectedItem = sidebar.Items.OfType<ListBoxItem>().FirstOrDefault(x => Equals(x.Tag, ProviderCatalog.Find(page) is not null ? "providers" : page)); refreshingSidebar = false;
    }
    private void SettingsChanged(object? sender, EventArgs e)
    {
        BuildSidebar();
        if (ProviderCatalog.Find(page) is not null) { UpdateProviderReading(page); UpdateProviderControlStates(); }
    }
    private void UpdateProviderControlStates()
    {
        foreach (var toggle in VisualChildren<CheckBox>(body))
            toggle.IsEnabled = System.Windows.Automation.AutomationProperties.GetName(toggle) switch
            {
                "Show additional limits" or "Show reset credits" => settings.Current.AccountLimitsEnabled,
                "Show estimated API-equivalent cost" => settings.Current.AnalyticsEnabled && page == "codex",
                "Show projects" or "Show sessions" => settings.Current.AnalyticsEnabled,
                "Show agent details" => settings.Current.AnalyticsEnabled && settings.Current.SessionsEnabled,
                "Show attachment metadata" => settings.Current.AnalyticsEnabled && settings.Current.SessionsEnabled && page == "codex",
                _ => true
            };
    }
    private void StoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        status.Text = store.IsRefreshing ? "Refreshing…" : store.Status;
        if (page == "usage") usagePane?.RefreshReadings();
        else if (ProviderCatalog.Find(page) is not null) UpdateProviderReading(page);
    }
    private string? renderedPage;
    private bool waitingForPicker;
    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }
    private void RenderAfterPicker()
    {
        var open = VisualChildren<System.Windows.Controls.ComboBox>(body).FirstOrDefault(x => x.IsDropDownOpen);
        if (open is null) { Render(); return; }
        if (waitingForPicker) return;
        waitingForPicker = true;
        EventHandler? closed = null;
        closed = (_, _) =>
        {
            open.DropDownClosed -= closed; waitingForPicker = false;
            if (page == "notch") Render();
        };
        open.DropDownClosed += closed;
    }
    private void Render()
    {
        var focusName = renderedPage == page
            ? VisualChildren<Control>(body).Where(x => x.IsKeyboardFocusWithin)
                .Select(System.Windows.Automation.AutomationProperties.GetName).FirstOrDefault(x => !string.IsNullOrEmpty(x))
            : null;
        renderedPage = page;
        body.Children.Clear();
        body.Margin = page == "usage" ? new Thickness(0) : new Thickness(0, 6, 0, 28);
        switch (page)
        {
            case "general": General(); break;
            case "usage": Usage(); break;
            case "notch": Notch(); break;
            case "providers": Providers(); break;
            case "diagnostics": Diagnostics(); break;
            case "about": About(); break;
            case "codex-accounts": body.Children.Add(new AccountsPane("codex", vault, store, settings)); break;
            case "claude-accounts": body.Children.Add(new AccountsPane("claude", vault, store, settings)); break;
            default: Provider(page); break;
        }
        if (focusName is not null)
            Dispatcher.BeginInvoke(new Action(() =>
                VisualChildren<Control>(body).FirstOrDefault(x => System.Windows.Automation.AutomationProperties.GetName(x) == focusName)?.Focus()));
    }
    private void Heading(string title, string subtitle)
    {
        body.Children.Add(Ui.Text(title, 27, weight: FontWeights.SemiBold)); body.Children.Add(Ui.Text(subtitle, color: "#B7B8BD"));
    }
    private void Save(AppSettings value)
    {
        try { settings.Save(value); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        { MessageBox.Show(this, "Settings could not be saved. Check access to the CodeRim data folder.", "CodeRim", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void General()
    {
        body.Children.Add(SettingsUi.Section("Startup",
            SettingsUi.Toggle("Launch at Login", settings.Current.LaunchAtLogin, x => Save(settings.Current with { LaunchAtLogin = x })),
            SettingsUi.Value("Status", settings.Current.LaunchAtLogin ? "Enabled" : "Disabled")));
        body.Children.Add(SettingsUi.Section("Refresh", SettingsUi.Picker("Mode", RefreshOptions, settings.Current.RefreshIntervalSeconds, x => Save(settings.Current with { RefreshIntervalSeconds = x }))));
        body.Children.Add(SettingsUi.Note("Automatic reacts to session changes with a one-minute fallback check."));
        body.Children.Add(SettingsUi.Section("Updates", SettingsUi.Toggle("Automatically check for updates", settings.Current.CheckForUpdates, x => Save(settings.Current with { CheckForUpdates = x }))));
        body.Children.Add(SettingsUi.Note("Checks GitHub once per day. Token usage data is never sent. Installation is manual."));
        body.Children.Add(SettingsUi.Section("Calendar", SettingsUi.Picker("Week starts on", Enum.GetValues<WeekStart>(), settings.Current.WeekStart, x => { Save(settings.Current with { WeekStart = x }); _ = store.RefreshAsync(); })));
        body.Children.Add(SettingsUi.Section("Usage Numbers",
            SettingsUi.Picker("Number format", Enum.GetValues<TokenNumberStyle>(), settings.Current.NumberStyle, x => Save(settings.Current with { NumberStyle = x })),
            SettingsUi.Toggle("Show cached input", settings.Current.ShowCachedInput, x => Save(settings.Current with { ShowCachedInput = x })),
            SettingsUi.Toggle("Show last updated", settings.Current.ShowLastUpdated, x => Save(settings.Current with { ShowLastUpdated = x }))));
        body.Children.Add(SettingsUi.Note("Applies to the Usage pane and the notch's tooltip."));
    }
    private UsagePane? usagePane;
    private void Usage()
    {
        usagePane ??= new UsagePane(store, settings, localProvider, Navigate);
        usagePane.RefreshReadings();
        body.Children.Add(usagePane);
    }
    private void Notch()
    {
        var shown = settings.Current.Visibility != NotchVisibility.Hidden;
        body.Children.Add(SettingsUi.Section("Edge Notch", SettingsUi.Toggle("Show edge notch", shown, x => { Save(settings.Current with {
            LastVisibleNotchMode = x ? settings.Current.LastVisibleNotchMode : settings.Current.Visibility,
            Visibility = x ? (settings.Current.LastVisibleNotchMode == NotchVisibility.AlwaysShow ? NotchVisibility.AlwaysShow : NotchVisibility.OnHover) : NotchVisibility.Hidden }); Render(); })));
        body.Children.Add(SettingsUi.Note("A floating usage ring welded to a screen edge. Alt-drag the pill to slide it along the edge; Recentre puts it back."));
        var controls = SettingsUi.Picker("Controls position", ControlOptions, settings.Current.ControlsPosition, x => Save(settings.Current with { ControlsPosition = x }));
        controls.IsEnabled = settings.Current.Edge is NotchEdge.Left or NotchEdge.Right;
        var placement = SettingsUi.Section("Placement",
            SettingsUi.Picker("Behaviour", new[] { NotchVisibility.OnHover, NotchVisibility.AlwaysShow }, shown ? settings.Current.Visibility : NotchVisibility.OnHover, x => Save(settings.Current with { Visibility = x })),
            SettingsUi.Picker("Edge", Enum.GetValues<NotchEdge>(), settings.Current.Edge, x => { Save(settings.Current with { Edge = x }); RenderAfterPicker(); }),
            SettingsUi.Picker("Size", ScaleOptions, settings.Current.Scale, x => Save(settings.Current with { Scale = x })),
            controls, SettingsUi.Action("Recentre", () => Save(settings.Current with { Offset = 0 })));
        placement.IsEnabled = shown; body.Children.Add(placement);
        body.Children.Add(SettingsUi.Note("Controls position applies to the left and right edges. Auto moves Settings and account controls above the notch when space below runs out."));
        var rows = new List<UIElement> { SettingsUi.Picker("Ring style", Enum.GetValues<RingColorMode>(), settings.Current.RingColor, x => { Save(settings.Current with { RingColor = x }); RenderAfterPicker(); }) };
        if (settings.Current.RingColor == RingColorMode.Gradient)
        {
            rows.Add(SettingsUi.Picker("Gradient", GradientOptions, settings.Current.Gradient, x => { Save(settings.Current with { Gradient = x }); RenderAfterPicker(); }));
            rows.Add(SettingsUi.Toggle("Animate gradient", settings.Current.AnimateGradient, x => Save(settings.Current with { AnimateGradient = x })));
        }
        else rows.Add(SettingsUi.Picker("Ring colour", AccentOptions, settings.Current.Accent, x => { Save(settings.Current with { Accent = x }); RenderAfterPicker(); }));
        var previews = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        foreach (var percent in new[] { 25d, 60d, 90d })
            previews.Children.Add(new ProviderRing { Settings = settings.Current, Reading = new ProviderReading("codex", ReadingState.Ready, [new LimitWindow("preview", "Preview", percent)], DateTimeOffset.Now), Margin = new Thickness(6) });
        rows.Add(SettingsUi.Row("Preview", new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(8), Child = previews }));
        var appearance = SettingsUi.Section("Appearance", rows.ToArray()); appearance.IsEnabled = shown; body.Children.Add(appearance);
        body.Children.Add(SettingsUi.Note("Usage colours reflect consumed quota. Fixed colour and Gradient keep the selected palette."));
        var readings = SettingsUi.Section("Readings",
            SettingsUi.Picker("Percentage", PercentageOptions, settings.Current.ShowRemaining ? "Remaining" : "Used", x => Save(settings.Current with { ShowRemaining = x == "Remaining" })),
            SettingsUi.Picker("Reset time", ResetOptions, settings.Current.ResetTime, x => Save(settings.Current with { ResetTime = x })),
            SettingsUi.Toggle("Show usage pace", settings.Current.ShowUsagePace, x => Save(settings.Current with { ShowUsagePace = x })));
        readings.IsEnabled = shown; body.Children.Add(readings);
        body.Children.Add(SettingsUi.Section("When a Session Ends",
            SettingsUi.Toggle("Peek the notch open", settings.Current.PeekOnCompletion, x => Save(settings.Current with { PeekOnCompletion = x })),
            SettingsUi.Toggle("Play a sound", settings.Current.CompletionSound, x => Save(settings.Current with { CompletionSound = x })),
            SettingsUi.Picker("Finished", SessionChime.Names, settings.Current.FinishedSound, x => { Save(settings.Current with { FinishedSound = x }); SessionChime.Play(x); }),
            SettingsUi.Picker("Blocked", SessionChime.Names, settings.Current.BlockedSound, x => { Save(settings.Current with { BlockedSound = x }); SessionChime.Play(x); })));
        body.Children.Add(SettingsUi.Section("Usage Alerts", SettingsUi.Toggle("Notify at 80% and 100% usage", settings.Current.AlertsEnabled, x => Save(settings.Current with { AlertsEnabled = x }))));
        body.Children.Add(SettingsUi.Note("Mute individual providers in Providers. Alerts always follow consumed usage."));
        var displays = System.Windows.Forms.Screen.AllScreens.Select(x => x.DeviceName).ToArray();
        body.Children.Add(SettingsUi.Section("Display",
            SettingsUi.Picker("Display", displays, settings.Current.Display ?? displays[0], x => Save(settings.Current with { Display = x })),
            SettingsUi.Toggle("Reduce motion", settings.Current.ReduceMotion, x => Save(settings.Current with { ReduceMotion = x }))));
    }
    private void Providers()
    {
        var rows = new List<UIElement>();
        foreach (var id in settings.Current.EnabledProviders)
        {
            var provider = ProviderCatalog.Find(id)!;
            var row = new DockPanel { Margin = new Thickness(14, 10, 14, 10) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var muted = settings.Current.MutedAlertProviders.Contains(id, StringComparer.Ordinal);
            var alerts = Ui.Toggle("", !muted, on => { Save(settings.Current with { MutedAlertProviders = on ? settings.Current.MutedAlertProviders.Where(x => x != id).ToArray() : [..settings.Current.MutedAlertProviders, id] }); });
            alerts.ToolTip = "Usage alerts for " + provider.Name;
            System.Windows.Automation.AutomationProperties.SetName(alerts, "Usage alerts for " + provider.Name);
            alerts.IsEnabled = settings.Current.AlertsEnabled; alerts.Width = 36; alerts.Margin = new Thickness(4, 0, 12, 0);
            actions.Children.Add(alerts);
            actions.Children.Add(Ui.Button("Details", () => Navigate(id)));
            var remove = Ui.Button("⊖", () => { Save(settings.Current with { EnabledProviders = settings.Current.EnabledProviders.Where(x => x != id).ToArray() }); Render(); });
            System.Windows.Automation.AutomationProperties.SetName(remove, "Remove " + provider.Name); actions.Children.Add(remove);
            DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
            if (settings.Current.EnabledProviders.Length > 1)
            {
                var handle = Ui.Button("⋮", () => { }); handle.ToolTip = "Drag to reorder " + provider.Name;
                System.Windows.Automation.AutomationProperties.SetName(handle, "Reorder " + provider.Name);
                handle.PreviewMouseMove += (_, e) => { if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragDrop.DoDragDrop(handle, new DataObject("CodeRim.Provider", id), DragDropEffects.Move); };
                var menu = new ContextMenu();
                foreach (var (title, direction) in new[] { ("Move earlier", -1), ("Move later", 1) })
                { var item = new MenuItem { Header = title }; item.Click += (_, _) => { MoveProvider(id, direction); Render(); }; menu.Items.Add(item); }
                handle.ContextMenu = menu; DockPanel.SetDock(handle, Dock.Left); row.Children.Add(handle);
                row.AllowDrop = true; row.Drop += (_, e) =>
                {
                    if (e.Data.GetData("CodeRim.Provider") is string moved && moved != id && settings.Current.EnabledProviders.Contains(moved, StringComparer.Ordinal))
                    {
                        var ids = settings.Current.EnabledProviders.ToList(); ids.Remove(moved); ids.Insert(ids.IndexOf(id), moved);
                        Save(settings.Current with { EnabledProviders = ids.ToArray() }); Render(); e.Handled = true;
                    }
                };
            }
            var mark = new Border { Width = 28, Height = 28, Background = Ui.Brush("#454545"), CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 10, 0), Child = new ProviderMark { ProviderId = id, Margin = new Thickness(5) } };
            DockPanel.SetDock(mark, Dock.Left); row.Children.Add(mark);
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = Ui.Text(provider.Name, 13, weight: FontWeights.SemiBold); name.Margin = new Thickness(0); labels.Children.Add(name);
            var reading = store.Readings.GetValueOrDefault(id);
            var detail = Ui.Text(reading?.Plan ?? reading?.Message ?? "Not connected", 11, "#A6A6AA"); detail.Margin = new Thickness(0, 2, 0, 0); labels.Children.Add(detail);
            row.Children.Add(labels); rows.Add(row);
        }
        if (rows.Count == 0) rows.Add(SettingsUi.Note("No providers added. Choose Add Provider to start monitoring."));
        rows.Add(SettingsUi.Action("+ Add Provider", ShowProviderPicker));
        body.Children.Add(SettingsUi.Section("Added Providers", rows.ToArray()));
        body.Children.Add(SettingsUi.Note("Remove a provider to stop its notch monitoring. You stay signed in to the original tool and can add it again at any time."));
        if (settings.Current.EnabledProviders.Length > 1) body.Children.Add(SettingsUi.Note("Drag a row by its handle to change the order the notch draws its rings."));
    }
    private void ShowProviderPicker()
    {
        var picker = new Window { Title = "Add Provider", Owner = this, Width = 620, Height = 580, MinWidth = 500, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        picker.SetResourceReference(BackgroundProperty, "WindowBackground");
        var layout = new DockPanel { Margin = new Thickness(20) }; picker.Content = layout;
        var close = Ui.Button("Done", picker.Close); close.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(close, Dock.Bottom); layout.Children.Add(close);
        var search = new TextBox { Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetName(search, "Search providers"); DockPanel.SetDock(search, Dock.Top); layout.Children.Add(search);
        var list = new StackPanel(); layout.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var order = ProviderCatalog.All.OrderBy(x => settings.Current.EnabledProviders.Contains(x.Id, StringComparer.Ordinal)).ToArray();
        void Populate()
        {
            list.Children.Clear();
            foreach (var provider in order.Where(x => x.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || x.Id.Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var added = settings.Current.EnabledProviders.Contains(provider.Id, StringComparer.Ordinal);
                var row = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
                var button = Ui.Button(added ? "Added" : "Add", () =>
                {
                    if (!settings.Current.EnabledProviders.Contains(provider.Id, StringComparer.Ordinal))
                    { Save(settings.Current with { EnabledProviders = [..settings.Current.EnabledProviders, provider.Id] }); _ = store.RefreshProviderAsync(provider.Id); Populate(); }
                });
                button.IsEnabled = !added; System.Windows.Automation.AutomationProperties.SetName(button, (added ? "Added " : "Add ") + provider.Name);
                DockPanel.SetDock(button, Dock.Right); row.Children.Add(button);
                var mark = new Border { Background = Ui.Brush("#454545"), CornerRadius = new CornerRadius(8), Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), Child = new ProviderMark { ProviderId = provider.Id, Margin = new Thickness(6) } };
                DockPanel.SetDock(mark, Dock.Left); row.Children.Add(mark);
                var text = new StackPanel(); text.Children.Add(Ui.Text(provider.Name, 14, weight: FontWeights.SemiBold)); text.Children.Add(Ui.Text(provider.Summary, 11, "#A6A6AA")); row.Children.Add(text); list.Children.Add(row);
            }
        }
        search.TextChanged += (_, _) => Populate(); Populate(); picker.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) picker.Close(); };
        picker.ShowDialog(); Render();
    }
    private static bool HasConnector(string id) => id is "codex" or "claude" or "jetbrains" || NativeProviders.Supported.Contains(id) || HttpProviders.Supported.Contains(id) || ScriptProviders.Catalog.ContainsKey(id);
    private void Provider(string id)
    {
        var provider = ProviderCatalog.Find(id); if (provider is null) { Navigate("providers"); return; }
        body.Children.Add(SettingsUi.Action("‹ All Providers", () => Navigate("providers")));
        var header = new DockPanel { Margin = new Thickness(14) };
        var refresh = Ui.AsyncButton("↻", () => store.RefreshProviderAsync(id));
        System.Windows.Automation.AutomationProperties.SetName(refresh, "Refresh " + provider.Name);
        DockPanel.SetDock(refresh, Dock.Right); header.Children.Add(refresh);
        var mark = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8),
            Background = Ui.Brush(id == "codex" ? "#30D158" : "#454545"),
            Margin = new Thickness(0, 0, 12, 0), Child = new ProviderMark { ProviderId = id, Margin = new Thickness(6) } };
        DockPanel.SetDock(mark, Dock.Left); header.Children.Add(mark);
        var headerText = new StackPanel();
        headerText.Children.Add(Ui.Text(provider.Name, 16, weight: FontWeights.SemiBold));
        headerText.Children.Add(Ui.Text(store.Readings.GetValueOrDefault(id)?.State.ToString() ?? "Available", 12, "#A6A6AA"));
        header.Children.Add(headerText);
        var headerCard = new Border { Child = header, CornerRadius = new CornerRadius(12), Margin = new Thickness(18, 0, 18, 4) };
        headerCard.SetResourceReference(Border.BackgroundProperty, "CardBackground"); body.Children.Add(headerCard);
        if (id is "codex" or "claude")
            body.Children.Add(SettingsUi.Section("Account",
                SettingsUi.Value("Account", SavedAccounts.CurrentAccountLabel(id, store.Synthetic) ?? "Not connected"),
                SettingsUi.Value("Plan", store.Readings.GetValueOrDefault(id)?.Plan ?? "Unavailable"),
                SettingsUi.Action("Manage Accounts…", () => Navigate(id + "-accounts"))));
        if (id == "codex")
            body.Children.Add(SettingsUi.Section("Limits",
                SettingsUi.Toggle("Show account limits", settings.Current.AccountLimitsEnabled, x => { Save(settings.Current with { AccountLimitsEnabled = x }); _ = store.RefreshProviderAsync(id); }),
                SettingsUi.Toggle("Show additional limits", settings.Current.AdditionalLimitsEnabled, x => Save(settings.Current with { AdditionalLimitsEnabled = x })),
                SettingsUi.Toggle("Show reset credits", settings.Current.ResetCreditsEnabled, x => Save(settings.Current with { ResetCreditsEnabled = x }))));
        if (provider.HasLocalHistory)
        {
            body.Children.Add(SettingsUi.Section("Usage Analytics",
                SettingsUi.Toggle("Show usage analytics", settings.Current.AnalyticsEnabled, x => Save(settings.Current with { AnalyticsEnabled = x })),
                SettingsUi.Toggle("Show estimated API-equivalent cost", settings.Current.CostEstimatesEnabled, x => Save(settings.Current with { CostEstimatesEnabled = x })),
                SettingsUi.Toggle("Show projects", settings.Current.ProjectsEnabled, x => Save(settings.Current with { ProjectsEnabled = x })),
                SettingsUi.Toggle("Show sessions", settings.Current.SessionsEnabled, x => Save(settings.Current with { SessionsEnabled = x })),
                SettingsUi.Toggle("Show agent details", settings.Current.AgentDetailsEnabled, x => Save(settings.Current with { AgentDetailsEnabled = x })),
                SettingsUi.Toggle("Show attachment metadata", settings.Current.AttachmentMetadataEnabled, x => Save(settings.Current with { AttachmentMetadataEnabled = x }))));
        }
        body.Children.Add(providerReading); UpdateProviderReading(id);
        var actions = new WrapPanel(); actions.Children.Add(Ui.AsyncButton("Refresh", () => store.RefreshProviderAsync(id)));
        actions.Children.Add(Ui.Button("Setup guide", () => OpenUrl(provider.GuideUrl))); body.Children.Add(actions);

        Ui.Section(body, "Connection");
        if (id == "codex")
        {
            body.Children.Add(Ui.Text("Uses the installed Codex app-server and its current sign-in. Local history is read independently."));
            body.Children.Add(Ui.Text(settings.Current.CodexExecutable ?? ProviderConnections.ResolveCodex() ?? "codex.exe has not been found", 11, "#B7B8BD"));
            body.Children.Add(Ui.Button("Choose codex.exe…", () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Codex executable|codex.exe", CheckFileExists = true };
                if (dialog.ShowDialog(this) == true) { Save(settings.Current with { CodexExecutable = dialog.FileName }); Render(); }
            }));
        }
        else if (id == "claude")
        {
            body.Children.Add(Ui.Text("Local history is read from Claude Code. Plan limits arrive through its status-line integration."));
            body.Children.Add(Ui.Text("Connect the SessionStart and status-line hooks, then start a new Claude session. Only rate-limit fields are stored.", 12, "#B7B8BD"));
            body.Children.Add(Ui.Button("Connect Claude status line", () =>
            {
                try
                {
                    if (ClaudeHookInstaller.HasOtherStatusLine() && MessageBox.Show(this, "Replace your current status line? CodeRim will keep a backup of settings.json.", "Connect Claude", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    ClaudeHookInstaller.Install(replaceExisting: true);
                    MessageBox.Show(this, "Connected. Start a new Claude Code session to read limits.", "CodeRim");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
                { MessageBox.Show(this, "Unable to update Claude settings safely. Check the Windows setup instructions.", "CodeRim"); }
            }));
            body.Children.Add(Ui.Button("Open Windows setup instructions", () => OpenUrl("https://github.com/dlfkdLR/CodeRim/blob/main/Documentation/WINDOWS.md")));
        }
        else if (id == "jetbrains") body.Children.Add(Ui.Text("Reads the latest AI Assistant quota from your JetBrains IDE settings. Enable AI Assistant and refresh its usage in the IDE."));
        else if (ScriptProviders.Catalog.TryGetValue(id, out var script))
        {
            foreach (var field in script.Settings)
            {
                var vaultKey = "setting:" + id + ":" + field.Key;
                body.Children.Add(Ui.Text(field.Title + " · " + field.Key, 12));
                if (field.Type == "secure") AddSecretField(vaultKey, id, "Save credential"); else AddSettingField(vaultKey, id);
            }
            if (script.CookieDomains.Length > 0)
            {
                body.Children.Add(Ui.Text("Cookie header for " + string.Join(", ", script.CookieDomains), 12));
                body.Children.Add(Ui.Text("Copy the Cookie header from your signed-in provider page. CodeRim stores it using Windows user encryption.", 11, "#B7B8BD"));
                AddSecretField("cookie:" + id, id, "Save cookie");
            }
        }
        else if (HasConnector(id) && id != "ollama-local")
        {
            if (id is "cursor" or "grok" or "opencode" or "commandcode" or "kilo" or "gemini-cli" or "vertexai" or "kiro") body.Children.Add(Ui.Text("Reads the provider’s existing local sign-in automatically. A saved credential overrides local discovery.", 12, "#A6A6AA"));
            if (id == "bedrock") body.Children.Add(Ui.Text("Uses your AWS CLI v2 profile, including SSO and assume-role sessions. Sign in with aws sso login first. Cost Explorer and CloudWatch permissions are required; AWS may charge for these queries.", 12, "#A6A6AA"));
            if (id == "kimi") body.Children.Add(Ui.Text("Use a Kimi Code API key (KIMI_CODE_API_KEY), not a Kimi web session token.", 12));
            if (id == "cursor") body.Children.Add(Ui.Text("Manual value: WorkosCursorSessionToken cookie header", 12));
            foreach (var field in NativeProviders.Settings(id))
            {
                var key = "setting:" + id + ":" + field.Key;
                if (field.Key.EndsWith("_ALLOW_BILLABLE_REQUESTS", StringComparison.Ordinal))
                {
                    body.Children.Add(Ui.Text("Each refresh can incur charges from this provider.", 12, "#B7B8BD"));
                    body.Children.Add(Ui.Toggle(field.Label, string.Equals(vault.Load(key) ?? Environment.GetEnvironmentVariable(field.Key), "true", StringComparison.OrdinalIgnoreCase), enabled =>
                    {
                        try { vault.Save(key, enabled ? "true" : "false"); store.InvalidateAccount(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { MessageBox.Show(this, "Could not save this setting.", "CodeRim"); }
                    }));
                }
                else { body.Children.Add(Ui.Text(field.Label)); if (field.Key.EndsWith("_TOKEN", StringComparison.Ordinal) || field.Key.EndsWith("_SECRET", StringComparison.Ordinal)) AddSecretField(key, id, "Save token"); else AddSettingField(key, id); }
            }
            if (id != "wayfinder") { body.Children.Add(Ui.Text(NativeProviders.CredentialLabel(id))); AddSecretField("provider:" + id, id, "Save credential"); }
        }
        else if (!HasConnector(id)) body.Children.Add(Ui.Text("This provider's Windows integration is still pending. Adding it does not create a live connection.", color: "#F2C66D"));
        Ui.Section(body, "Notch order");
        var order = new WrapPanel(); order.Children.Add(Ui.Button("Move earlier", () => MoveProvider(id, -1))); order.Children.Add(Ui.Button("Move later", () => MoveProvider(id, 1)));
        order.Children.Add(Ui.Button("Remove from notch", () => { Save(settings.Current with { EnabledProviders = settings.Current.EnabledProviders.Where(x => x != id).ToArray() }); Navigate("providers"); })); body.Children.Add(order);
        if (provider.HasLocalHistory)
        {
            Ui.Section(body, "Local data"); body.Children.Add(Ui.Button("Show usage", () => { localProvider = id; Navigate("usage"); usagePane?.SelectProvider(id); }));
            body.Children.Add(Ui.Button("Clear local history…", () =>
            {
                if (MessageBox.Show(this, "Clear CodeRim's stored history for " + provider.Name + "? Original session files will be preserved. Earlier events will not be imported again.", "Clear local history", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                { store.Clear(id); _ = store.RefreshAsync(); }
            }));
        }
        foreach (var child in body.Children.OfType<FrameworkElement>())
            if (child.Margin.Left == 0 && child.Margin.Right == 0)
                child.Margin = new Thickness(18, child.Margin.Top, 18, child.Margin.Bottom);
        UpdateProviderControlStates();
    }
    private void UpdateProviderReading(string id)
    {
        providerReading.Children.Clear(); var reading = ProviderDisplayPolicy.Apply(store.Readings.GetValueOrDefault(id), settings.Current);
        providerReading.Children.Add(Ui.Text(reading?.Message ?? reading?.State.ToString() ?? "Waiting for the first reading", color: "#B7B8BD"));
        foreach (var window in reading?.Windows ?? []) providerReading.Children.Add(Ui.Row(window.Name, window.UsedPercent is { } p ? $"{p:0.#}% used" + (window.DisplayValue is { } description ? " · " + description : "") : window.DisplayValue ?? "—"));
    }
    private void AddSettingField(string key, string id)
    {
        var input = new TextBox { Text = vault.Load(key) ?? "", MaxLength = 4096, Margin = new Thickness(0, 4, 0, 8) };
        body.Children.Add(input);
        System.Windows.Automation.AutomationProperties.SetName(input, key.Split(':').Last());
        body.Children.Add(Ui.Button("Save setting", () =>
        {
            try { vault.Save(key, input.Text.Trim()); store.InvalidateAccount(id); _ = store.RefreshProviderAsync(id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { MessageBox.Show(this, "Could not save this setting.", "CodeRim"); }
        }));
    }
    private void AddSecretField(string key, string id, string label)
    {
        var password = new PasswordBox { MaxLength = 32768, Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 8) }; body.Children.Add(password);
        System.Windows.Automation.AutomationProperties.SetName(password, label);
        var result = Ui.Text("", 11, "#B7B8BD");
        body.Children.Add(Ui.Button(label, () =>
        {
            if (string.IsNullOrWhiteSpace(password.Password)) return;
            try { vault.Save(key, password.Password.Trim()); password.Clear(); store.InvalidateAccount(id); result.Text = "Saved."; _ = store.RefreshProviderAsync(id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { result.Text = "Could not save the setting."; }
        }));
        body.Children.Add(Ui.Button("Remove saved value", () =>
        {
            try { vault.Delete(key); store.InvalidateAccount(id); result.Text = "Removed."; _ = store.RefreshProviderAsync(id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { result.Text = "Could not remove the setting."; }
        })); body.Children.Add(result);
    }
    private void MoveProvider(string id, int direction)
    {
        var providers = settings.Current.EnabledProviders.ToArray(); var index = Array.IndexOf(providers, id); var next = index + direction;
        if (index < 0 || next < 0 || next >= providers.Length) return;
        (providers[index], providers[next]) = (providers[next], providers[index]); Save(settings.Current with { EnabledProviders = providers });
    }
    private void Diagnostics()
    {
        var cliStatus = Ui.Text("Use coderim in a new terminal after installation.", 11, "#A6A6AA");
        body.Children.Add(SettingsUi.Section("CLI", SettingsUi.Action("Install CLI", () =>
        {
            try { cliStatus.Text = "Installed at " + CliInstaller.Install() + ". Open a new terminal."; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { cliStatus.Text = "CLI installation failed. Keep the complete release package in a writable permanent folder."; }
        })));
        cliStatus.Margin = new Thickness(32, 8, 32, 0); body.Children.Add(cliStatus);
        body.Children.Add(SettingsUi.Section("Diagnostics",
            SettingsUi.Toggle("Enable debug logging", settings.Current.DebugLogging, x => Save(settings.Current with { DebugLogging = x })),
            SettingsUi.Action("Open Log Folder", () => { Directory.CreateDirectory(AppDiagnostics.LogDirectory); CredentialVault.RestrictDirectory(AppDiagnostics.LogDirectory); OpenUrl(AppDiagnostics.LogDirectory); })));
        body.Children.Add(SettingsUi.Note("Never includes prompts, responses, source code, terminal output, or authentication tokens."));
        body.Children.Add(SettingsUi.Section("Codex Account Limit Source",
            SettingsUi.Value("Mode", "Automatic"), SettingsUi.Value("Provider", "Codex app-server")));
        body.Children.Add(SettingsUi.Note("Read-only local RPC request — no reset or purchase actions."));
        body.Children.Add(SettingsUi.Section("Local Data",
            SettingsUi.Value("Scope", "This PC · Across accounts"),
            SettingsUi.Action("Open Data Folder", () => OpenUrl(CompanionFile.DataDirectory)),
            Ui.AsyncButton("Rescan local sources", () => { store.Invalidate(null); return store.RefreshAsync(true); })));
        body.Children.Add(SettingsUi.Note(store.Status));
    }
    private void About()
    {
        var identity = new StackPanel { Margin = new Thickness(18, 14, 18, 10), HorizontalAlignment = HorizontalAlignment.Center };
        identity.Children.Add(new Image { Width = 60, Height = 60, Margin = new Thickness(0, 0, 0, 12),
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/CodeRim.ico")) });
        identity.Children.Add(Ui.Text("CodeRim", 22, weight: FontWeights.SemiBold));
        identity.Children.Add(Ui.Text("Version " + ReleaseUpdates.CurrentVersion, 14, "#A6A6AA"));
        body.Children.Add(identity);
        body.Children.Add(SettingsUi.Section("Application",
            SettingsUi.Value("Version", ReleaseUpdates.CurrentVersion.ToString()),
            SettingsUi.Value("Platform", "Windows · " + UpdateNotifications.Architecture),
            SettingsUi.Value("Data scope", "Local history + optional account limits"),
            SettingsUi.Value("Privacy", "Local numeric history; encrypted credentials")));
        var updateStatus = Ui.Text("", 12, "#A6A6AA");
        var checkUpdate = Ui.AsyncButton("Check for updates", async () =>
        {
            updateStatus.Text = "Checking…";
            try
            {
                var update = await ReleaseUpdates.CheckAsync(UpdateNotifications.Architecture).ConfigureAwait(true);
                updateStatus.Text = update.IsNewer ? "CodeRim " + update.Version + " is available." : "You are using the latest Windows release.";
                if (update.IsNewer && MessageBox.Show(this, "Download CodeRim " + update.Version + " for Windows?", "CodeRim update", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    OpenUrl(update.Download.AbsoluteUri);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { updateStatus.Text = "Could not check Windows updates. Try again or open the releases page."; }
        });
        checkUpdate.HorizontalAlignment = HorizontalAlignment.Left; checkUpdate.Margin = new Thickness(14, 9, 14, 9);
        body.Children.Add(SettingsUi.Section("Updates", checkUpdate));
        updateStatus.Margin = new Thickness(32, 6, 32, 0); body.Children.Add(updateStatus);
        body.Children.Add(SettingsUi.Note("Checks GitHub releases. Token usage data is never sent. Installation is manual."));
        body.Children.Add(SettingsUi.Section("Project",
            SettingsUi.Action("Open Source on GitHub", () => OpenUrl("https://github.com/dlfkdLR/CodeRim")),
            SettingsUi.Action("View Releases", () => OpenUrl("https://github.com/dlfkdLR/CodeRim/releases")),
            SettingsUi.Action("Read MIT License", () => OpenUrl("https://github.com/dlfkdLR/CodeRim/blob/main/LICENSE")),
            SettingsUi.Action("Windows documentation", () => OpenUrl("https://github.com/dlfkdLR/CodeRim/blob/main/Documentation/WINDOWS.md"))));
        body.Children.Add(SettingsUi.Note("Notch design and supporting code: Codenotch, MIT © 2026 Vinz. Provider reference integrations: CodexBar. Provider logos belong to their respective owners. See the bundled LICENSE and NOTICE."));
    }
    private void OpenAccounts(string provider)
    {
        if (!accountWindows.TryGetValue(provider, out var window))
        {
            window = new Window { Title = (provider == "codex" ? "Codex" : "Claude") + " Accounts", Width = 560, Height = 400, MinWidth = 500, MinHeight = 300, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            window.SetResourceReference(BackgroundProperty, "WindowBackground"); window.SetResourceReference(ForegroundProperty, "PrimaryText");
            window.Content = new AccountsPane(provider, vault, store, settings);
            window.Closed += (_, _) => accountWindows.Remove(provider); accountWindows[provider] = window;
        }
        window.Show(); window.Activate();
    }
    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Win32Exception) { }
    }
}
