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
    private static readonly string[] ResetOptions = ["Absolute", "Relative"];
    private static readonly int[] RefreshOptions = [-1, 30, 60, 120, 300, 900, 1800, 0];
    private static readonly double[] ScaleOptions = new[] { 0.8, 1.0, 1.25 };
    private static readonly string[] AccentOptions = new[] { "system", "#00FF88", "#3B9CFF", "#9B7DFF", "#FF6EC7", "#FF9F3F" };
    private static readonly string[] GradientOptions = new[] { "Aurora", "Ocean", "Sunset", "Spectrum" };
    private readonly DashboardStore store;
    private readonly AppSettingsStore settings;
    private readonly CredentialVault vault;
    private readonly ListBox sidebar = new() { BorderThickness = new Thickness(0), Padding = new Thickness(10, 10, 10, 0) };
    private ScrollViewer? contentViewport;
    private readonly StackPanel body = new() { Margin = new Thickness(0, 6, 0, 28) };
    private readonly Dictionary<string, Window> accountWindows = new(StringComparer.Ordinal);
    private readonly TextBlock status = Ui.Text("");
    private readonly StackPanel providerReading = new();
    private readonly Dictionary<string, Action> providerListDetails = new(StringComparer.Ordinal);
    private TextBlock? gradientMotionNote;
    private Action? refreshNotchVisibility;
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
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.CurrentAndNext }; layout.Children.Add(splitter);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        contentViewport = scroll;
        Grid.SetColumn(scroll, 1); layout.Children.Add(scroll); ConfigureShell(layout, splitter);
        sidebar.SelectionChanged += (_, _) => { if (!refreshingSidebar && sidebar.SelectedItem is ListBoxItem item && item.Tag is string id) Navigate(id); };
        settings.SettingsChanged += SettingsChanged; store.PropertyChanged += StoreChanged; Motion.PolicyChanged += UpdateNotchMotionNote;
        Activated += (_, _) => RefreshStartupStatus();
        Closed += (_, _) => { updateWindowClosed = true; CancelUpdateOperation(); settings.SettingsChanged -= SettingsChanged; store.PropertyChanged -= StoreChanged; Motion.PolicyChanged -= UpdateNotchMotionNote; };
        PreviewKeyDown += (_, e) => { if (!e.Handled) e.Handled = HandleWindowShortcut(e.Key, System.Windows.Input.Keyboard.Modifiers); };
        BuildSidebar(); Navigate("usage");
    }
    internal bool HandleWindowShortcut(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        // AltGr is represented as Ctrl+Alt on international keyboards. It must never
        // activate Quit (AltGr+Q commonly types @) or other application commands.
        if (modifiers is not (System.Windows.Input.ModifierKeys.Control or
            (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))) return false;
        if (modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            switch (key)
            {
                case System.Windows.Input.Key.OemComma: Present(); return true;
                case System.Windows.Input.Key.U: Navigate("usage"); Present(); return true;
                case System.Windows.Input.Key.Q: ((App)System.Windows.Application.Current).ShutdownApplication(); return true;
                case System.Windows.Input.Key.R: _ = store.RefreshAsync(true); return true;
            }
        }
        return page == "usage" && usagePane is not null && usagePane.HandleShortcut(key, modifiers);
    }
    internal void HideToTray()
    {
        // Closing Settings still cancels updater work and closes owned account windows.
        // Keep the reusable view and its revision alive, as the Mac controller does.
        CancelUpdateOperation();
        foreach (Window owned in OwnedWindows.Cast<Window>().ToArray()) owned.Close();
        Hide();
    }
    internal void Present()
    {
        if (sidebarHidden) ToggleSidebar();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
    }
    public void Navigate(string? id)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (id?.StartsWith("sessions:", StringComparison.Ordinal) == true)
        {
            localProvider = id[9..]; page = "usage"; BuildSidebar(); Render(); usagePane?.SelectProvider(localProvider); usagePane?.ShowSessions(); Show(); Activate(); return;
        }
        if (id is "codex-accounts" or "claude-accounts") { OpenAccounts(id.Split('-')[0]); return; }
        page = id ?? "general";
        if (ProviderCatalog.Find(page) is { } provider && page is not ("codex" or "claude") && !settings.Current.EnabledProviders.Contains(provider.Id, StringComparer.Ordinal)) page = "providers";
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
        BuildSidebar(); UpdateNotchMotionNote(); refreshNotchVisibility?.Invoke();
        if (page == "providers") UpdateProviderList();
        foreach (var ring in VisualChildren<ProviderRing>(body)) ring.Settings = settings.Current;
        RefreshStartupStatus();
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
                "Notify at 80% and 100%" => settings.Current.AlertsEnabled,
                "Enable Claude Code" => !store.Claude.ChangingConnection,
                _ => true
            };
    }
    private void StoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        status.Text = store.IsRefreshing ? "Refreshing…" : store.Status;
        if (page == "usage") usagePane?.RefreshReadings();
        else if (page == "providers") UpdateProviderList();
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
        var changedPage = renderedPage != page;
        renderedPage = page; UpdateSectionTitle();
        CancelUpdateOperation(); updateViewRevision++; manualUpdateCheck = null; refreshNotchVisibility = null;
        body.Children.Clear(); providerListDetails.Clear(); ResetProviderAlerts(); ResetProviderAccount();
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
        if (changedPage) { contentViewport?.ScrollToTop(); Motion.Enter(body); }
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
        AddStartupSection();
        body.Children.Add(SettingsUi.Section("Refresh", SettingsUi.Picker("Mode", RefreshOptions, settings.Current.AutomaticRefresh ? -1 : settings.Current.RefreshIntervalSeconds, x => Save(settings.Current with { RefreshIntervalSeconds = x == -1 ? 60 : x, AutomaticRefresh = x == -1 }))));
        body.Children.Add(SettingsUi.Note("Automatic reacts to session changes with a one-minute fallback check."));
        body.Children.Add(SettingsUi.Section("Updates", SettingsUi.Toggle("Automatically check for updates", settings.Current.CheckForUpdates, x => Save(settings.Current with { CheckForUpdates = x }))));
        body.Children.Add(SettingsUi.Note("Checks GitHub once per day and downloads verified updates for Setup installations. Restart from Information to install. Token usage data is never sent."));
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
    private void UpdateNotchMotionNote()
    {
        if (gradientMotionNote is not null) gradientMotionNote.Text = settings.Current.ReduceMotion || !Motion.Enabled
            ? "Paused while Reduce Motion is enabled in Windows or CodeRim settings." : "The gradient colours flow smoothly around the ring.";
    }
    private void Notch()
    {
        gradientMotionNote = null;
        var shown = settings.Current.Visibility != NotchVisibility.Hidden;
        var notchSections = new List<FrameworkElement>();
        var syncingVisibility = false;
        var showToggle = (CheckBox)SettingsUi.Toggle("Show edge notch", shown, x =>
        {
            if (syncingVisibility) return;
            Save(settings.Current with { Visibility = x ? settings.Current.LastVisibleNotchMode : NotchVisibility.Hidden });
            refreshNotchVisibility?.Invoke();
        });
        body.Children.Add(SettingsUi.Section("Edge Notch", showToggle));
        body.Children.Add(SettingsUi.Note("A floating usage ring welded to a screen edge. Alt-drag the pill to slide it along the edge; Recentre puts it back."));
        var controls = SettingsUi.Picker("Controls position", ControlOptions, settings.Current.ControlsPosition, x => Save(settings.Current with { ControlsPosition = x }));
        controls.IsEnabled = settings.Current.Edge is NotchEdge.Left or NotchEdge.Right;
        var behaviourRow = SettingsUi.Picker("Behaviour", new[] { NotchVisibility.OnHover, NotchVisibility.AlwaysShow },
            settings.Current.LastVisibleNotchMode, x => { if (!syncingVisibility) Save(settings.Current with { Visibility = x }); });
        var behaviour = VisualChildren<ComboBox>(behaviourRow).Single();
        var placement = SettingsUi.Section("Placement", behaviourRow,
            SettingsUi.Picker("Edge", Enum.GetValues<NotchEdge>(), settings.Current.Edge, x => { Save(settings.Current with { Edge = x }); RenderAfterPicker(); }),
            SettingsUi.Picker("Size", ScaleOptions, settings.Current.Scale, x => Save(settings.Current with { Scale = x })),
            controls, SettingsUi.Action("Recentre", () => Save(settings.Current with { Offset = 0 })));
        placement.IsEnabled = shown; notchSections.Add(placement); body.Children.Add(placement);
        body.Children.Add(SettingsUi.Note("Controls position applies to the left and right edges. Auto moves Settings and account controls above the notch when space below runs out."));
        var rows = new List<UIElement> { SettingsUi.Picker("Ring style", Enum.GetValues<RingColorMode>(), settings.Current.RingColor, x => { Save(settings.Current with { RingColor = x }); RenderAfterPicker(); }) };
        if (settings.Current.RingColor == RingColorMode.Gradient)
        {
            rows.Add(SettingsUi.Picker("Gradient", GradientOptions, settings.Current.Gradient, x => { Save(settings.Current with { Gradient = x }); RenderAfterPicker(); }));
            rows.Add(SettingsUi.Toggle("Animate gradient", settings.Current.AnimateGradient, x => Save(settings.Current with { AnimateGradient = x })));
            gradientMotionNote = SettingsUi.Note(""); UpdateNotchMotionNote(); rows.Add(gradientMotionNote);
        }
        else rows.Add(SettingsUi.Picker("Ring colour", AccentOptions, settings.Current.Accent, x => { Save(settings.Current with { Accent = x }); RenderAfterPicker(); }));
        var previews = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        foreach (var percent in new[] { 25d, 60d, 90d })
            previews.Children.Add(new ProviderRing { Settings = settings.Current, Reading = new ProviderReading("codex", ReadingState.Ready, [new LimitWindow("preview", "Preview", percent)], DateTimeOffset.Now), Margin = new Thickness(6) });
        rows.Add(SettingsUi.Row("Preview", new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(8), Child = previews }));
        var appearance = SettingsUi.Section("Appearance", rows.ToArray()); appearance.IsEnabled = shown; notchSections.Add(appearance); body.Children.Add(appearance);
        body.Children.Add(SettingsUi.Note(settings.Current.RingColor switch
        {
            RingColorMode.Usage => "The ring uses your chosen colour below 50%, yellow from 50%, and orange from 70%.",
            RingColorMode.Fixed => "The ring keeps your chosen colour at every usage level. Limit alerts stay enabled according to your settings.",
            _ => "The ring keeps the same gradient at every usage level. Limit alerts stay enabled according to your settings."
        }));
        var readings = SettingsUi.Section("Readings",
            SettingsUi.Picker("Percentage", PercentageOptions, settings.Current.ShowRemaining ? "Remaining" : "Used", x => Save(settings.Current with { ShowRemaining = x == "Remaining" })),
            SettingsUi.Picker("Reset time", ResetOptions, settings.Current.ResetTime, x => Save(settings.Current with { ResetTime = x })),
            SettingsUi.Toggle("Show usage pace", settings.Current.ShowUsagePace, x => Save(settings.Current with { ShowUsagePace = x })));
        readings.IsEnabled = shown; notchSections.Add(readings); body.Children.Add(readings);
        var taskActivity = SettingsUi.Section("Task Activity",
            SettingsUi.Toggle("Show tasks with unknown status", settings.Current.ShowUnknownSessions, x => { Save(settings.Current with { ShowUnknownSessions = x }); _ = store.RefreshActivityAsync(); }, caption: "Include recent tasks whose live status cannot be checked, such as remote tasks. They may already be finished."),
            SettingsUi.Toggle("Show task duration", settings.Current.ShowSessionDuration, x => Save(settings.Current with { ShowSessionDuration = x }), caption: "Show time spent in the current working or waiting state. Unknown tasks have no duration."),
            SettingsUi.Toggle("Show tokens per chat", settings.Current.ShowSessionTokens, x => Save(settings.Current with { ShowSessionTokens = x }), caption: "Include sub-agent usage in the main chat total. Chats without usage records stay blank."));
        taskActivity.IsEnabled = shown; notchSections.Add(taskActivity); body.Children.Add(taskActivity);
        var finished = SettingsUi.Picker("Finished", SessionChime.Names, settings.Current.FinishedSound, x => { Save(settings.Current with { FinishedSound = x }); if (settings.Current.CompletionSound && settings.Current.Visibility != NotchVisibility.Hidden) SessionChime.Play(x); });
        var blocked = SettingsUi.Picker("Blocked", SessionChime.Names, settings.Current.BlockedSound, x => { Save(settings.Current with { BlockedSound = x }); if (settings.Current.CompletionSound && settings.Current.Visibility != NotchVisibility.Hidden) SessionChime.Play(x); });
        finished.IsEnabled = blocked.IsEnabled = settings.Current.CompletionSound;
        var sessionEnd = SettingsUi.Section("When a Session Ends",
            SettingsUi.Toggle("Peek the notch open", settings.Current.PeekOnCompletion, x => Save(settings.Current with { PeekOnCompletion = x })),
            SettingsUi.Toggle("Play a sound", settings.Current.CompletionSound, x => { Save(settings.Current with { CompletionSound = x }); finished.IsEnabled = blocked.IsEnabled = x; }),
            finished, blocked);
        sessionEnd.IsEnabled = shown; notchSections.Add(sessionEnd); body.Children.Add(sessionEnd);
        var alerts = SettingsUi.Section("Usage Alerts", SettingsUi.Toggle("Notify at 80% and 100% usage", settings.Current.AlertsEnabled, x => Save(settings.Current with { AlertsEnabled = x })));
        alerts.IsEnabled = shown; notchSections.Add(alerts); body.Children.Add(alerts);
        refreshNotchVisibility = () =>
        {
            syncingVisibility = true;
            try
            {
                var visible = settings.Current.Visibility != NotchVisibility.Hidden;
                showToggle.IsChecked = visible;
                behaviour.SelectedItem = settings.Current.LastVisibleNotchMode;
                foreach (var section in notchSections) section.IsEnabled = visible;
            }
            finally { syncingVisibility = false; }
        };
        body.Children.Add(SettingsUi.Note("Mute individual providers in Providers. Alerts always follow consumed usage."));
        var displays = System.Windows.Forms.Screen.AllScreens.Select(x => x.DeviceName).ToArray();
        body.Children.Add(SettingsUi.Section("Display",
            SettingsUi.Picker("Display", displays, settings.Current.Display ?? displays[0], x => Save(settings.Current with { Display = x })),
            SettingsUi.Toggle("Reduce motion", settings.Current.ReduceMotion, x => Save(settings.Current with { ReduceMotion = x }))));
    }
    private void UpdateProviderList()
    {
        foreach (var update in providerListDetails.Values) update();
    }
    private void Providers()
    {
        var rows = new List<UIElement>();
        foreach (var id in settings.Current.EnabledProviders)
        {
            var provider = ProviderCatalog.Find(id)!;
            var row = new DockPanel { Margin = new Thickness(20, 9, 20, 9), MinHeight = 36 };
            var content = new ProviderAccountRow(id, store, settings, () => Navigate(id), () =>
            {
                Save(settings.Current with { EnabledProviders = settings.Current.EnabledProviders.Where(x => x != id).ToArray() }); Render();
            }, muted => Save(settings.Current with { MutedAlertProviders = muted
                ? settings.Current.MutedAlertProviders.Append(id).Distinct(StringComparer.Ordinal).ToArray()
                : settings.Current.MutedAlertProviders.Where(x => x != id).ToArray() }));
            providerListDetails[id] = content.Refresh;
            if (settings.Current.EnabledProviders.Length > 1)
            {
                var handle = Ui.Button("☰", () => { }); handle.ToolTip = "Drag to reorder " + provider.Name;
                handle.Width = 20; handle.MinHeight = 20; handle.Padding = new Thickness(0); handle.Margin = new Thickness(0, 0, 10, 0); handle.Background = Brushes.Transparent; handle.BorderThickness = new Thickness(0); handle.FontSize = 11;
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
            row.Children.Add(content); rows.Add(row);
        }
        if (rows.Count == 0) rows.Add(SettingsUi.Note("No providers added. Choose Add Provider to start monitoring."));
        rows.Add(SettingsUi.Action("+ Add Provider", ShowProviderPicker));
        body.Children.Add(SettingsUi.Section("Added Providers", rows.ToArray()));
        body.Children.Add(SettingsUi.Note("Remove a provider to stop its notch monitoring. You stay signed in to the original tool and can add it again at any time."));
        if (settings.Current.EnabledProviders.Length > 1) body.Children.Add(SettingsUi.Note("Drag a row by its handle to change the order the notch draws its rings."));
    }
    private void ShowProviderPicker()
    {
        var picker = new ProviderPickerWindow(this, store, settings, id =>
        {
            if (settings.Current.EnabledProviders.Contains(id, StringComparer.Ordinal)) return;
            Save(settings.Current with { EnabledProviders = [..settings.Current.EnabledProviders, id] });
            _ = store.RefreshProviderAsync(id);
        }, id => Navigate(id));
        picker.ShowDialog(); Render();
    }
    private static bool HasConnector(string id) => id is "codex" or "claude" or "jetbrains" || NativeProviders.Supported.Contains(id) || HttpProviders.Supported.Contains(id) || ScriptProviders.Catalog.ContainsKey(id);
    private void Provider(string id)
    {
        var provider = ProviderCatalog.Find(id); if (provider is null) { Navigate("providers"); return; }
        var accountDisplay = store.AccountDisplay(id);
        body.Children.Add(SettingsUi.Action("‹ All Providers", () => Navigate("providers")));
        var header = new DockPanel { Margin = new Thickness(14) };
        if (id == "claude") AddClaudeHeader(header);
        var refresh = Ui.RefreshButton("Refresh " + provider.Name, async () =>
        { if (id == "claude") await store.Claude.RefreshAsync(); await store.RefreshProviderAsync(id); });
        if (id == "claude") claudeRefresh = refresh;
        System.Windows.Automation.AutomationProperties.SetAutomationId(refresh, "provider.refresh");
        DockPanel.SetDock(refresh, Dock.Right); header.Children.Add(refresh);
        if (!provider.HasLocalHistory) AddProviderAlertButton(header, id, provider.Name);
        var mark = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8),
            Background = Ui.Brush(id == "codex" ? "#30D158" : "#454545"),
            Margin = new Thickness(0, 0, 12, 0), Child = new ProviderMark { ProviderId = id, Margin = new Thickness(6) } };
        DockPanel.SetDock(mark, Dock.Left); header.Children.Add(mark);
        var headerText = new StackPanel();
        headerText.Children.Add(Ui.Text(provider.Name, 16, weight: FontWeights.SemiBold));
        headerText.Children.Add(ProviderValue("provider.status", ProviderStatus(id, accountDisplay.Reading), 12));
        header.Children.Add(headerText);
        var headerCard = new Border { Child = header, CornerRadius = new CornerRadius(12), Margin = new Thickness(18, 0, 18, 4) };
        headerCard.SetResourceReference(Border.BackgroundProperty, "CardBackground"); body.Children.Add(headerCard);
        if (id == "claude") AddClaudeAccount();
        if (id == "codex")
        {
            var accountRows = new List<UIElement> {
                SettingsUi.Row("Account", ProviderValue("provider.account", accountDisplay.Label ?? "Not connected")),
                SettingsUi.Row("Plan", ProviderValue("provider.plan", accountDisplay.Plan ?? "Unavailable"))
            };
            if (id == "claude") accountRows.Add(SettingsUi.Action("Manage Accounts…", () => Navigate(id + "-accounts")));
            body.Children.Add(SettingsUi.Section("Account", accountRows.ToArray()));
            if (id == "codex") body.Children.Add(SettingsUi.Note("Add or switch accounts from the account menu in Settings ▸ Usage."));
        }
        if (id == "codex")
            body.Children.Add(SettingsUi.Section("Limits",
                SettingsUi.Toggle("Show account limits", settings.Current.AccountLimitsEnabled, x => { Save(settings.Current with { AccountLimitsEnabled = x }); _ = store.RefreshProviderAsync(id); }),
                SettingsUi.Toggle("Show additional limits", settings.Current.AdditionalLimitsEnabled, x => Save(settings.Current with { AdditionalLimitsEnabled = x })),
                SettingsUi.Toggle("Show reset credits", settings.Current.ResetCreditsEnabled, x => Save(settings.Current with { ResetCreditsEnabled = x }))));
        if (provider.HasLocalHistory)
        {
            var analytics = new List<UIElement> {
                SettingsUi.Toggle("Show usage analytics", settings.Current.AnalyticsEnabled, x => Save(settings.Current with { AnalyticsEnabled = x }))
            };
            if (id == "codex") analytics.Add(SettingsUi.Toggle("Show estimated API-equivalent cost", settings.Current.CostEstimatesEnabled, x => Save(settings.Current with { CostEstimatesEnabled = x })));
            analytics.AddRange([
                SettingsUi.Toggle("Show projects", settings.Current.ProjectsEnabled, x => Save(settings.Current with { ProjectsEnabled = x })),
                SettingsUi.Toggle("Show sessions", settings.Current.SessionsEnabled, x => Save(settings.Current with { SessionsEnabled = x })),
                SettingsUi.Toggle("Show agent details", settings.Current.AgentDetailsEnabled, x => Save(settings.Current with { AgentDetailsEnabled = x })),
                SettingsUi.Toggle("Show attachment metadata", settings.Current.AttachmentMetadataEnabled, x => Save(settings.Current with { AttachmentMetadataEnabled = x }))]);
            body.Children.Add(SettingsUi.Section("Usage Analytics", analytics.ToArray()));
            body.Children.Add(SettingsUi.Note(id == "codex"
                ? "Estimated from current API prices — not a bill or a quota prediction."
                : "Comes from Claude Code session logs on this PC. Five-hour and weekly limits appear after a connected account completes a response; cost estimates aren't available yet."));
        }
        if (id is "codex" or "claude")
        {
            AddProviderLocalData(id, provider.Name);
            UpdateProviderReading(id); UpdateProviderControlStates(); return;
        }
        body.Children.Add(providerReading);
        if (!provider.HasLocalHistory) AddProviderAccount(id);
        UpdateProviderReading(id);
        var connectionStart = body.Children.Count;
        if (provider.HasLocalHistory) Ui.Section(body, "Connection");
        if (id == "copilot") body.Children.Add(Ui.Text("Uses your current GitHub CLI sign-in. Sign in with gh auth login, or provide an access token below.", 12));
        if (id == "glm") body.Children.Add(Ui.Text("Detects a GLM login from Claude Code, ZCode or OpenCode. A key entered below takes precedence.", 12));
        if (id == "codebuff") body.Children.Add(Ui.Text("Uses your current Codebuff CLI sign-in. A key entered below takes precedence.", 12));
        if (id == "jetbrains") body.Children.Add(Ui.Text("Reads the latest AI Assistant quota from your JetBrains IDE settings. Enable AI Assistant and refresh its usage in the IDE."));
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
            if (id == "stepfun") body.Children.Add(Ui.Text("Auto uses your saved sign-in or username and password. Manual keeps the selected Oasis-Token and can refresh it without changing accounts.", 12));
            if (id == "kimi") body.Children.Add(Ui.Text("Auto tries your API key, a fresh Kimi Code CLI sign-in, then the selected Web session. Expired CLI credentials require signing in again.", 12));
            if (id == "cursor") body.Children.Add(Ui.Text("Manual value: WorkosCursorSessionToken cookie header", 12));
            foreach (var field in NativeProviders.Settings(id))
            {
                var key = "setting:" + id + ":" + field.Key;
                if (field.Key == "ALIBABA_CODING_PLAN_SOURCE")
                {
                    var selected = ProviderConnections.AlibabaCodingSource(vault) == "web" ? "Web" : "API";
                    body.Children.Add(SettingsUi.Picker("Usage source", AlibabaCodingSources, selected, value =>
                    {
                        try { vault.Save(key, value.ToLowerInvariant()); store.InvalidateAccount(id); Navigate(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { MessageBox.Show(this, "Could not save the usage source.", "CodeRim"); }
                    }));
                    body.Children.Add(Ui.Text("API keys and Web sessions stay separate. Web reads the selected region's Coding Plan quotas.", 11, "#A6A6AA"));
                }
                else if (field.Key == "ALIBABA_CODING_PLAN_REGION")
                {
                    var selected = ProviderConnections.AlibabaCodingRegion(vault) == "cn" ? "China" : "International";
                    body.Children.Add(SettingsUi.Picker("Region", MoonshotRegions, selected, value =>
                    {
                        try { vault.Save(key, value == "China" ? "cn" : "intl"); store.InvalidateAccount(id); Navigate(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { MessageBox.Show(this, "Could not save the region.", "CodeRim"); }
                    }));
                }
                else if (field.Key == "ALIBABA_TOKEN_PLAN_SOURCE")
                {
                    var selected = ProviderConnections.AlibabaSource(vault) switch { "cli" => "CLI", "web" => "Web", _ => "Auto" };
                    body.Children.Add(SettingsUi.Picker("Usage source", AlibabaSources, selected, value =>
                    {
                        try { vault.Save(key, value.ToLowerInvariant()); store.InvalidateAccount(id); Navigate(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { MessageBox.Show(this, "Could not save the usage source.", "CodeRim"); }
                    }));
                    body.Children.Add(Ui.Text("Auto tries Bailian CLI, then the selected Web session. CLI shows the active personal 5-hour and weekly quotas; Web follows the selected Team or Personal plan.", 11, "#A6A6AA"));
                }
                else if (field.Key == "ALIBABA_TOKEN_PLAN_REGION")
                {
                    var region = AlibabaTokenPlanCliUsage.Region(ProviderConnections.EffectiveSetting(vault, id, field.Key));
                    var selected = region switch { "cn" => "China · Team", "cn-personal" => "China · Personal", "intl-personal" => "International · Personal", _ => "International · Team" };
                    body.Children.Add(SettingsUi.Picker("Region and Web plan", AlibabaRegions, selected, value =>
                    {
                        try
                        {
                            vault.Save(key, value switch { "China · Team" => "cn", "China · Personal" => "cn-personal", "International · Personal" => "intl-personal", _ => "intl" });
                            store.InvalidateAccount(id); Navigate(id); _ = store.RefreshProviderAsync(id);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { MessageBox.Show(this, "Could not save the region.", "CodeRim"); }
                    }));
                }
                else if (field.Key == "ALIBABA_TOKEN_PLAN_EXECUTABLE")
                {
                    if (ProviderConnections.AlibabaSource(vault) == "web") continue;
                    body.Children.Add(Ui.Text(field.Label)); AddSettingField(key, id);
                    body.Children.Add(Ui.Button("Choose bl.exe…", () =>
                    {
                        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Bailian executable|bl.exe", CheckFileExists = true };
                        if (dialog.ShowDialog(this) != true) return;
                        try { vault.Save(key, dialog.FileName); store.InvalidateAccount(id); Navigate(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { MessageBox.Show(this, "Could not save the selected path.", "CodeRim"); }
                    }));
                    body.Children.Add(Ui.Button("Bailian CLI installation and sign-in", () => OpenUrl("https://docs.agent.bailian.aliyun.com/en/bailian-cli/getting-started/installation")));
                }
                else if (field.Key == "ALIBABA_TOKEN_PLAN_SEC_TOKEN" && ProviderConnections.AlibabaSource(vault) == "cli") continue;
                else if (field.Key == "STEPFUN_AUTH_MODE")
                {
                    var selected = StepFunAuthentication.Mode(ProviderConnections.EffectiveSetting(vault, id, field.Key)) == "manual" ? "Manual" : "Auto";
                    body.Children.Add(SettingsUi.Picker(field.Label, StepFunModes, selected, value =>
                    {
                        try { vault.Save(key, value.ToLowerInvariant()); store.InvalidateAccount(id); Navigate(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { MessageBox.Show(this, "Could not save the authentication mode.", "CodeRim"); }
                    }));
                }
                else if (id == "stepfun" && StepFunAuthentication.Mode(ProviderConnections.EffectiveSetting(vault, id, "STEPFUN_AUTH_MODE")) == "manual") continue;
                else if (field.Key == "DEEPSEEK_DETAILED_USAGE")
                {
                    if (DeepSeekAuthentication.Source(ProviderConnections.EffectiveSetting(vault, id, "DEEPSEEK_USAGE_SOURCE")) == "api") continue;
                    body.Children.Add(SettingsUi.Toggle("Detailed Web usage", DeepSeekUsageDetails.Enabled(
                        ProviderConnections.EffectiveSetting(vault, id, field.Key)), enabled =>
                    {
                        try { vault.Save(key, enabled ? "true" : "false"); store.InvalidateAccount(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { MessageBox.Show(this, "Could not save the usage preference.", "CodeRim"); }
                    }));
                    body.Children.Add(Ui.Text("Shows daily and period totals from the selected platform session. API-key readings remain separate.", 11, "#A6A6AA"));
                }
                else if (field.Key is "DEEPSEEK_USAGE_SOURCE" or "KIMI_USAGE_SOURCE" or "MINIMAX_USAGE_SOURCE")
                {
                    var selected = DeepSeekAuthentication.Source(ProviderConnections.EffectiveSetting(vault, id, field.Key)) switch { "api" => "API", "web" => "Web", _ => "Auto" };
                    body.Children.Add(SettingsUi.Picker("Usage source", DeepSeekSources, selected, value =>
                    {
                        try { vault.Save(key, value.ToLowerInvariant()); store.InvalidateAccount(id); Navigate(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { MessageBox.Show(this, "Could not save the usage source.", "CodeRim"); }
                    }));
                    body.Children.Add(Ui.Text(id == "minimax" ? "Auto uses the selected Web session when present, otherwise the API key. Each region keeps separate connections."
                        : id == "kimi" ? "API, CLI and Web keep their account data separate. Web uses only its selected session."
                        : "Auto uses an API key when present, then a platform session. Each source keeps its own credential.", 11, "#A6A6AA"));
                }
                else if (field.Key == "MINIMAX_REGION")
                {
                    ProviderConnections.BindMiniMaxLegacy(vault);
                    var selected = MiniMaxAuthentication.Region(ProviderConnections.EffectiveSetting(vault, id, field.Key)) == "cn" ? "China" : "Global";
                    body.Children.Add(SettingsUi.Picker("Region", MiniMaxRegions, selected, value =>
                    {
                        try { ProviderConnections.BindMiniMaxLegacy(vault); vault.Save(key, value == "China" ? "cn" : "global"); store.InvalidateAccount(id); Navigate(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { MessageBox.Show(this, "Could not save the region.", "CodeRim"); }
                    }));
                }
                else if (field.Key == "MOONSHOT_REGION")
                {
                    var selected = MoonshotAuthentication.Region(ProviderConnections.EffectiveSetting(vault, id, field.Key)) == "china" ? "China" : "International";
                    body.Children.Add(SettingsUi.Picker("Region", MoonshotRegions, selected, value =>
                    {
                        try { vault.Save(key, value.ToLowerInvariant()); store.InvalidateAccount(id); Navigate(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { MessageBox.Show(this, "Could not save the region.", "CodeRim"); }
                    }));
                    body.Children.Add(Ui.Text("Each region keeps its own API key. Switching regions does not copy an existing key.", 11, "#A6A6AA"));
                }
                else if (field.Key == "ANTIGRAVITY_USAGE_SOURCE")
                {
                    var selected = AntigravityLocalUsage.Source(ProviderConnections.EffectiveSetting(vault, id, field.Key)) == "local" ? "Local IDE" : "OAuth";
                    body.Children.Add(SettingsUi.Picker("Usage source", AntigravitySources, selected, value =>
                    {
                        try { vault.Save(key, value == "Local IDE" ? "local" : "oauth"); store.InvalidateAccount(id); Navigate(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { MessageBox.Show(this, "Could not save the usage source.", "CodeRim"); }
                    }));
                    body.Children.Add(Ui.Text("Local IDE reads quota from one running Antigravity session on this PC. OAuth uses your separately saved connection.", 11, "#A6A6AA"));
                }
                else if (id == "gemini" && AntigravityLocalUsage.Source(ProviderConnections.EffectiveSetting(vault, id, "ANTIGRAVITY_USAGE_SOURCE")) == "local") continue;
                else if (field.Key == "WINDSURF_USAGE_SOURCE")
                {
                    var selected = WindsurfLocalUsage.Source(ProviderConnections.EffectiveSetting(vault, id, field.Key)) == "local" ? "Local" : "Web";
                    body.Children.Add(SettingsUi.Picker("Usage source", WindsurfSources, selected, value =>
                    {
                        try { vault.Save(key, value.ToLowerInvariant()); store.InvalidateAccount(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { MessageBox.Show(this, "Could not save the usage source.", "CodeRim"); }
                    }));
                    body.Children.Add(Ui.Text("Web verifies your supplied sign-in. Local reads Windsurf's saved quota; its freshness and current account are not verified.", 11, "#A6A6AA"));
                }
                else if (field.Key == "WINDSURF_CACHE_PATH")
                {
                    body.Children.Add(Ui.Text(field.Label)); AddSettingField(key, id);
                    body.Children.Add(Ui.Button("Choose state.vscdb…", () =>
                    {
                        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Windsurf state database|state.vscdb|SQLite database|*.vscdb;*.sqlite;*.db", CheckFileExists = true };
                        if (dialog.ShowDialog(this) != true) return;
                        try { vault.Save(key, dialog.FileName); store.InvalidateAccount(id); Navigate(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { MessageBox.Show(this, "Could not save the selected path.", "CodeRim"); }
                    }));
                }
                else if (field.Key == "AMP_USAGE_SOURCE")
                {
                    var selected = AmpCliUsage.Source(ProviderConnections.EffectiveSetting(vault, id, field.Key)) switch { "cli" => "CLI", "web" => "Web", _ => "API" };
                    body.Children.Add(SettingsUi.Picker("Usage source", AmpSources, selected, value =>
                    {
                        try { vault.Save(key, value.ToLowerInvariant()); store.InvalidateAccount(id); Navigate(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { MessageBox.Show(this, "Could not save the usage source.", "CodeRim"); }
                    }));
                    body.Children.Add(Ui.Text("API reads subscription and balance details with an API key. CLI uses the Amp sign-in on this PC. Web reads Amp Free with your browser session.", 11, "#A6A6AA"));
                }
                else if (field.Key.EndsWith("_ALLOW_BILLABLE_REQUESTS", StringComparison.Ordinal))
                {
                    body.Children.Add(Ui.Text("Each refresh can incur charges from this provider.", 12, "#B7B8BD"));
                    body.Children.Add(Ui.Toggle(field.Label, string.Equals(vault.Load(key) ?? Environment.GetEnvironmentVariable(field.Key), "true", StringComparison.OrdinalIgnoreCase), enabled =>
                    {
                        try { vault.Save(key, enabled ? "true" : "false"); store.InvalidateAccount(id); _ = store.RefreshProviderAsync(id); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { MessageBox.Show(this, "Could not save this setting.", "CodeRim"); }
                    }));
                }
                else { body.Children.Add(Ui.Text(field.Label)); if (field.Key.EndsWith("_TOKEN", StringComparison.Ordinal) || field.Key.EndsWith("_SECRET", StringComparison.Ordinal) || field.Key.EndsWith("_PASSWORD", StringComparison.Ordinal)) AddSecretField(key, id, field.Key.EndsWith("_PASSWORD", StringComparison.Ordinal) ? "Save password" : "Save token"); else AddSettingField(key, id); }
            }
            if (id == "kimi")
            {
                var source = KimiAuthentication.Source(ProviderConnections.EffectiveSetting(vault, id, "KIMI_USAGE_SOURCE"));
                if (source is "auto" or "api") { body.Children.Add(Ui.Text("Kimi Code API key")); AddSecretField("provider:kimi", id, "Save API key"); }
                if (source is "auto" or "web")
                {
                    var desktop = vault.Load(KimiDesktopConnection.StorageKey);
                    if (desktop is null) { body.Children.Add(Ui.Text("Kimi Web session token or cookie")); AddSecretField("cookie:kimi", id, "Save Web session"); }
                    else
                    {
                        body.Children.Add(Ui.Text("Web session: Kimi Desktop", 12));
                        body.Children.Add(Ui.Text("The selected Desktop sign-in is read again on each refresh.", 11, "#A6A6AA"));
                        body.Children.Add(Ui.Button("Use saved Web session", () =>
                        {
                            try { vault.Delete(KimiDesktopConnection.StorageKey); store.InvalidateAccount(id); Render(); _ = store.RefreshProviderAsync(id); }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                            { MessageBox.Show(this, "Could not change the Kimi connection.", "CodeRim"); }
                        }));
                    }
                    body.Children.Add(Ui.Button(desktop is null ? "Connect Kimi Desktop…" : "Change Desktop connection…",
                        () => KimiDesktopConnection.Import(this, vault, () => { store.InvalidateAccount(id); Render(); _ = store.RefreshProviderAsync(id); })));
                }
            }
            else if (id == "alibaba")
            {
                if (ProviderConnections.AlibabaCodingSource(vault) == "api")
                { body.Children.Add(Ui.Text("Alibaba Coding Plan API key")); AddSecretField("provider:alibaba", id, "Save API key"); }
                else if (ProviderConnections.AlibabaCodingRegion(vault) is not null)
                { body.Children.Add(Ui.Text("Coding Plan Web session cookie")); AddSecretField(ProviderConnections.AlibabaCodingWebKey(vault), id, "Save Web session"); }
            }
            else if (id == "alibabatokenplan")
            {
                if (ProviderConnections.AlibabaSource(vault) is "auto" or "web")
                { body.Children.Add(Ui.Text("Token Plan Web session cookie")); AddSecretField("provider:" + id, id, "Save Web session"); }
            }
            else if (id == "minimax")
            {
                var region = MiniMaxAuthentication.Region(ProviderConnections.EffectiveSetting(vault, id, "MINIMAX_REGION"));
                var source = MiniMaxAuthentication.Source(ProviderConnections.EffectiveSetting(vault, id, "MINIMAX_USAGE_SOURCE"));
                if (region is not null)
                {
                    if (source is "auto" or "api") { body.Children.Add(Ui.Text("MiniMax Coding Plan API key")); AddSecretField("provider:minimax:" + region, id, "Save API key"); }
                    if (source is "auto" or "web") { body.Children.Add(Ui.Text("MiniMax Web cookie or copied cURL request")); AddSecretField("cookie:minimax:" + region, id, "Save Web session"); }
                }
            }
            else if (id == "deepseek")
            {
                var source = DeepSeekAuthentication.Source(ProviderConnections.EffectiveSetting(vault, id, "DEEPSEEK_USAGE_SOURCE"));
                if (source is "auto" or "api") { body.Children.Add(Ui.Text("DeepSeek API key")); AddSecretField("provider:deepseek", id, "Save API key"); }
                if (source is "auto" or "web") { body.Children.Add(Ui.Text("DeepSeek platform session token")); AddSecretField("provider:deepseek:web", id, "Save platform session"); }
            }
            else if (id == "moonshot")
            {
                var region = MoonshotAuthentication.Region(ProviderConnections.EffectiveSetting(vault, id, "MOONSHOT_REGION"));
                if (region is not null) { body.Children.Add(Ui.Text(region == "china" ? "Moonshot China API key" : "Moonshot International API key")); AddSecretField("provider:moonshot:" + region, id, "Save credential"); }
            }
            else if (id == "amp")
            {
                var source = AmpCliUsage.Source(ProviderConnections.EffectiveSetting(vault, "amp", "AMP_USAGE_SOURCE"));
                if (source == "web") { body.Children.Add(Ui.Text("Amp Web session cookie")); AddSecretField("cookie:amp", id, "Save cookie"); }
                else if (source == "api") { body.Children.Add(Ui.Text("Amp API key")); AddSecretField("provider:amp", id, "Save credential"); }
            }
            else if (id != "wayfinder" && (id != "gemini" || AntigravityLocalUsage.Source(ProviderConnections.EffectiveSetting(vault, id, "ANTIGRAVITY_USAGE_SOURCE")) == "oauth")) { body.Children.Add(Ui.Text(NativeProviders.CredentialLabel(id))); AddSecretField("provider:" + id, id, "Save credential"); }
        }
        else if (!HasConnector(id)) body.Children.Add(Ui.Text("This provider's Windows integration is still pending. Adding it does not create a live connection.", color: "#F2C66D"));
        AddChromiumConnection(id);
        if (BrowserConnections.Domains(id).Length > 0 && (id != "alibaba" || ProviderConnections.AlibabaCodingSource(vault) == "web" && ProviderConnections.AlibabaCodingRegion(vault) is not null) && (id != "alibabatokenplan" || ProviderConnections.AlibabaSource(vault) is "auto" or "web") && (id != "minimax" || MiniMaxAuthentication.Source(ProviderConnections.EffectiveSetting(vault, id, "MINIMAX_USAGE_SOURCE")) is "auto" or "web") && (id != "stepfun" || StepFunAuthentication.Mode(ProviderConnections.EffectiveSetting(vault, id, "STEPFUN_AUTH_MODE")) == "auto") && (id != "kimi" || vault.Load(KimiDesktopConnection.StorageKey) is null && KimiAuthentication.Source(ProviderConnections.EffectiveSetting(vault, id, "KIMI_USAGE_SOURCE")) is "auto" or "web") && (id != "amp" || AmpCliUsage.Source(ProviderConnections.EffectiveSetting(vault, "amp", "AMP_USAGE_SOURCE")) == "web"))
        {
            body.Children.Add(Ui.Button("Import from Firefox…", () => BrowserConnections.Import(this, id, vault, settings.Current, () =>
            { ClearChromiumForBrowser(id); store.InvalidateAccount(id); _ = store.RefreshProviderAsync(id); })));
            body.Children.Add(Ui.Button("Remove imported sign-in", () =>
            {
                try { vault.Delete(BrowserConnections.StorageKey(id, vault)); store.InvalidateAccount(id); _ = store.RefreshProviderAsync(id); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { MessageBox.Show(this, "Could not remove the imported sign-in.", "CodeRim"); }
            }));
        }
        if (!provider.HasLocalHistory) GroupProviderConnection(connectionStart, provider.GuideUrl);
        if (provider.HasLocalHistory)
        {
            Ui.Section(body, "Notch order");
            var order = new WrapPanel(); order.Children.Add(Ui.Button("Move earlier", () => MoveProvider(id, -1))); order.Children.Add(Ui.Button("Move later", () => MoveProvider(id, 1)));
            order.Children.Add(Ui.Button("Remove from notch", () => { Save(settings.Current with { EnabledProviders = settings.Current.EnabledProviders.Where(x => x != id).ToArray() }); Navigate("providers"); })); body.Children.Add(order);
        }
        else AddProviderAlerts(id);
        if (provider.HasLocalHistory)
        {
            AddProviderLocalData(id, provider.Name);
        }
        foreach (var child in body.Children.OfType<FrameworkElement>())
            if (child != providerReading && child.Margin.Left == 0 && child.Margin.Right == 0)
                child.Margin = new Thickness(18, child.Margin.Top, 18, child.Margin.Bottom);
        UpdateProviderControlStates();
    }
    private static readonly string[] AlibabaCodingSources = ["API", "Web"];
    private static readonly string[] DeepSeekSources = ["Auto", "API", "Web"];
    private static readonly string[] MoonshotRegions = ["International", "China"];
    private static readonly string[] AntigravitySources = ["OAuth", "Local IDE"];
    private static readonly string[] AmpSources = ["API", "CLI", "Web"];
    private static readonly string[] WindsurfSources = ["Web", "Local"];
    private static TextBlock ProviderValue(string identifier, string text, double size = 13)
    {
        var label = Ui.Text(text, size, "#A6A6AA");
        System.Windows.Automation.AutomationProperties.SetAutomationId(label, identifier);
        return label;
    }
    private void UpdateProviderReading(string id)
    {
        UpdateProviderLocalData(id);
        // Update the existing labels so credential drafts and keyboard focus survive a poll.
        var display = store.AccountDisplay(id);
        var current = display.Reading?.Evaluated(DateTimeOffset.Now);
        UpdateProviderAlerts(id, current);
        UpdateProviderAccount(current);
        foreach (var label in VisualChildren<TextBlock>(body))
        {
            switch (System.Windows.Automation.AutomationProperties.GetAutomationId(label))
            {
                case "provider.status": label.Text = ProviderStatus(id, current); break;
                case "provider.account": label.Text = display.Label ?? "Not connected"; break;
                case "provider.plan":
                    label.Text = display.Plan ?? "Unavailable";
                    if (page == "codex" && label.Parent is FrameworkElement planRow) planRow.Visibility = display.Plan is null ? Visibility.Collapsed : Visibility.Visible;
                    break;
            }
        }
        RenderProviderLimits(ProviderDisplayPolicy.Apply(current, settings.Current));
        if (id == "claude") UpdateClaude();
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
    private static readonly string[] AlibabaSources = ["Auto", "CLI", "Web"];
    private static readonly string[] AlibabaRegions = ["International · Team", "International · Personal", "China · Team", "China · Personal"];
    private static readonly string[] MiniMaxRegions = ["Global", "China"];
    private static readonly string[] StepFunModes = ["Auto", "Manual"];
    private void AddSecretField(string key, string id, string label)
    {
        var password = new PasswordBox { MaxLength = id == "factory" ? 262144 : id == "minimax" ? 65536 : 32768, Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 8) }; body.Children.Add(password);
        System.Windows.Automation.AutomationProperties.SetName(password, label);
        var result = Ui.Text("", 11, "#B7B8BD");
        body.Children.Add(Ui.Button(label, () =>
        {
            if (key == "setting:stepfun:STEPFUN_PASSWORD" ? password.Password.Length == 0 : string.IsNullOrWhiteSpace(password.Password)) return;
            if (key.StartsWith("cookie:minimax:", StringComparison.Ordinal) && MiniMaxAuthentication.Parse(password.Password, key.Split(':')[^1]) is null)
            { result.Text = "Enter a valid cookie or copied request for this region."; return; }
            try { vault.Save(key, key == "setting:stepfun:STEPFUN_PASSWORD" ? password.Password : password.Password.Trim()); if (key == "provider:" + id && id != "amp" || key == "cookie:" + id) vault.Delete("browser:" + id); if (key.StartsWith("cookie:minimax:", StringComparison.Ordinal)) vault.Delete("browser:minimax:" + key.Split(':')[^1]); ClearChromiumForManual(id, key); password.Clear(); store.InvalidateAccount(id); result.Text = "Saved."; _ = store.RefreshProviderAsync(id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { result.Text = "Could not save the setting."; }
        }));
        body.Children.Add(Ui.Button("Remove saved value", () =>
        {
            try { vault.Delete(key); if (id == "minimax" && key == "provider:minimax:" + vault.Load("setting:minimax:LEGACY_REGION")) vault.Delete("provider:minimax"); store.InvalidateAccount(id); result.Text = "Removed."; _ = store.RefreshProviderAsync(id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { result.Text = "Could not remove the setting."; }
        })); body.Children.Add(result);
    }
    private void MoveProvider(string id, int direction)
    {
        var providers = settings.Current.EnabledProviders.ToArray(); var index = Array.IndexOf(providers, id); var next = index + direction;
        if (index < 0 || next < 0 || next >= providers.Length) return;
        (providers[index], providers[next]) = (providers[next], providers[index]); Save(settings.Current with { EnabledProviders = providers });
    }
    private void ChooseCodexExecutable()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Codex executable|codex.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) { Save(settings.Current with { CodexExecutable = dialog.FileName }); Render(); }
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
            SettingsUi.Value("Mode", "Automatic"), SettingsUi.Value("Provider", "Codex app-server"),
            SettingsUi.Value("Executable", settings.Current.CodexExecutable ?? ProviderConnections.ResolveCodex() ?? "codex.exe has not been found"),
            SettingsUi.Action("Choose codex.exe…", ChooseCodexExecutable),
            SettingsUi.Action("Open Windows setup instructions", () => OpenUrl("https://github.com/dlfkdLR/CodeRim/blob/main/Documentation/WINDOWS.md"))));
        body.Children.Add(SettingsUi.Note("Read-only local RPC request — no reset or purchase actions."));
        body.Children.Add(SettingsUi.Section("Local Data",
            SettingsUi.Value("Scope", "This PC · Across accounts"),
            SettingsUi.Action("Open Data Folder", () => OpenUrl(CompanionFile.DataDirectory)),
            Ui.AsyncButton("Rescan local sources", () => { store.Invalidate(null); return store.RefreshAsync(true); })));
        body.Children.Add(SettingsUi.Note(store.Status));
    }
    private void About()
    {
        var identity = new StackPanel { Margin = new Thickness(18, 6, 18, 4), HorizontalAlignment = HorizontalAlignment.Center };
        identity.Children.Add(new Image { Width = 64, Height = 64, Margin = new Thickness(0, 0, 0, 8),
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/CodeRim.ico")) });
        identity.Children.Add(Ui.Text("CodeRim", 17, weight: FontWeights.SemiBold));
        identity.Children.Add(Ui.Text("Version " + ReleaseUpdates.CurrentVersion, 13, "#A6A6AA"));
        foreach (var text in identity.Children.OfType<TextBlock>()) text.TextAlignment = TextAlignment.Center;
        body.Children.Add(identity);
        body.Children.Add(SettingsUi.Section("Application",
            SettingsUi.Value("Version", ReleaseUpdates.CurrentVersion.ToString()),
            SettingsUi.Value("Build", System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyFileVersionAttribute>(typeof(App).Assembly)?.Version ?? "Development"),
            SettingsUi.Value("Data scope", "Local history + ChatGPT account totals"),
            SettingsUi.Value("Privacy", "Local numeric history; account totals in memory")));
        AddUpdateSection();
        body.Children.Add(SettingsUi.Section("Project",
            SettingsUi.Link("Open Source on GitHub", new("https://github.com/dlfkdLR/CodeRim"), "M5,2 L1,7 L5,12 M10,2 L14,7 L10,12 M9,0 L6,14", OpenUrl),
            SettingsUi.Link("View Releases", new("https://github.com/dlfkdLR/CodeRim/releases"), "M1,4 L8,1 L15,4 V12 L8,15 L1,12 Z M1,4 L8,7 L15,4 M8,7 V15", OpenUrl),
            SettingsUi.Link("Read MIT License", BundledNotice("LICENSE"), "M3,1 H10 L14,5 V15 H3 Z M10,1 V5 H14 M5,8 H12 M5,11 H12", OpenUrl),
            SettingsUi.Link("Codenotch - MIT License", BundledNotice("NOTICE"), "M3,1 H10 L14,5 V15 H3 Z M10,1 V5 H14 M5,8 H12 M5,11 H12", OpenUrl)));
        body.Children.Add(SettingsUi.Note("Includes code and design adapted from Codenotch. Copyright © 2026 Vinz, MIT License."));
        body.Children.Add(SettingsUi.Note("CodeRim is an independent utility and is not affiliated with or endorsed by OpenAI or Anthropic."));
    }
    private static Uri BundledNotice(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name + ".txt");
        return File.Exists(path) ? new Uri(path) : new Uri("https://github.com/dlfkdLR/CodeRim/blob/main/" + name);
    }
    private void OpenAccounts(string provider)
    {
        if (!accountWindows.TryGetValue(provider, out var window))
        {
            window = new Window { Title = (provider == "codex" ? "Codex" : "Claude") + " Accounts", Width = 560, Height = 400, MinWidth = 500, MinHeight = 300, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            window.SetResourceReference(BackgroundProperty, "WindowBackground"); window.SetResourceReference(ForegroundProperty, "PrimaryText");
            var pane = new AccountsPane(provider, vault, store, settings);
            window.Content = pane;
            var accountWindow = window;
            var clientSizeInitialized = false;
            window.Loaded += (_, _) =>
            {
                if (clientSizeInitialized) return;
                clientSizeInitialized = true;
                accountWindow.UpdateLayout();
                // macOS utility sizes describe content. WPF Window sizes include
                // the title bar and resize frame, so retain the same client area.
                var chromeWidth = Math.Max(0, accountWindow.ActualWidth - pane.ActualWidth - pane.Margin.Left - pane.Margin.Right);
                var chromeHeight = Math.Max(0, accountWindow.ActualHeight - pane.ActualHeight - pane.Margin.Top - pane.Margin.Bottom);
                accountWindow.MinWidth = 500 + chromeWidth;
                accountWindow.MinHeight = 300 + chromeHeight;
                accountWindow.Width = 560 + chromeWidth;
                accountWindow.Height = 400 + chromeHeight;
            };
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
