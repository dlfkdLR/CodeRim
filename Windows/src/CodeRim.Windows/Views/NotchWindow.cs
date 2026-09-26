using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;
using Button = System.Windows.Controls.Button;
using Screen = System.Windows.Forms.Screen;

namespace CodeRim.Windows.Views;

internal sealed partial class NotchWindow : Window
{
    private readonly DashboardStore store;
    private readonly AppSettingsStore settings;
    private readonly Action<string?> openSettings;
    private readonly DispatcherTimer foldTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer hoverClear = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool accountMenu;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly Popup popup = new() { AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Custom };
    // Keep the measured Popup child stable across same-sized reading refreshes.
    // WPF positions an assigned unmeasured child at size0 and does not raise a
    // native resize when its eventual size equals the preceding child's size.
    private readonly Border popupFrame = new();
    private readonly List<ProviderRing> rings = [];
    private readonly Dictionary<string, Button> buttons = new(StringComparer.Ordinal);
    private bool expanded, pinned, trackingMenu, dragging, closed;
    private NotchVisibility? lastVisibility;
    private string? hovered;
    private Point dragStart;
    private double dragOffset;
    private ScrollViewer? viewport;
    private double bodyLength, bodyDepth, bodyStart;
    private bool Vertical => settings.Current.Edge is NotchEdge.Left or NotchEdge.Right;
    internal bool Expanded => expanded || pinned || settings.Current.Visibility == NotchVisibility.AlwaysShow;
    internal bool PopupIsOpen => popup.IsOpen;
    internal bool AccountMenuIsOpen => accountMenu && popup.IsOpen;
    internal FrameworkElement? PopupContent => popupFrame.Child as FrameworkElement;

    public NotchWindow(DashboardStore store, AppSettingsStore settings, Action<string?> openSettings)
    {
        this.store = store; this.settings = settings; this.openSettings = openSettings;
        Title = "CodeRim notch"; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; ShowActivated = false;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        AutomationProperties.SetName(this, "CodeRim provider usage notch");
        popup.PlacementTarget = this;
        popup.Child = popupFrame;
        controlHide.Tick += (_, _) => HideControlsIfUnused();
        popup.CustomPopupPlacementCallback = PlacePopup;
        store.PropertyChanged += Update; settings.SettingsChanged += SettingsChanged;
        MouseEnter += (_, _) => { foldTimer.Stop(); if (!Expanded) SetExpanded(true); };
        MouseLeave += (_, _) => foldTimer.Start();
        LostKeyboardFocus += (_, _) => foldTimer.Start();
        Deactivated += (_, _) => foldTimer.Start();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DismissPopupFromKeyboard(); e.Handled = true; } };
        hoverClear.Tick += (_, _) => DismissProviderCard();
        popup.Closed += (_, _) => { accountMenu = false; if (!closed) foldTimer.Start(); };
        foldTimer.Tick += (_, _) => TryFold();
        clock.Tick += (_, _) => RefreshPopupClock();
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
            dragging = true; dragStart = System.Windows.Forms.Control.MousePosition.ToPoint(); dragOffset = settings.Current.Offset;
            CaptureMouse(); e.Handled = true;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (!dragging) return;
            var point = System.Windows.Forms.Control.MousePosition.ToPoint();
            Position(dragOffset + (Vertical ? point.Y - dragStart.Y : point.X - dragStart.X) / ScreenScale(SelectedScreen()));
            e.Handled = true;
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            var point = System.Windows.Forms.Control.MousePosition.ToPoint(); dragging = false; ReleaseMouseCapture();
            settings.Save(settings.Current with { Offset = dragOffset + (Vertical ? point.Y - dragStart.Y : point.X - dragStart.X) / ScreenScale(SelectedScreen()) });
            e.Handled = true;
        };
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
            Render();
        };
        Closed += (_, _) =>
        {
            closed = true; popup.IsOpen = false; foldTimer.Stop(); hoverClear.Stop(); controlHide.Stop(); clock.Stop(); Motion.Stop(this); Motion.Stop(popupFrame);
            store.PropertyChanged -= Update; settings.SettingsChanged -= SettingsChanged;
            SystemParameters.StaticPropertyChanged -= DisplayChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
        };
        SystemParameters.StaticPropertyChanged += DisplayChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        clock.Start();
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE: refresh does not steal the active editor.
        if (message == 0x02E0) Dispatcher.BeginInvoke(() => Render());
        return IntPtr.Zero;
    }
    private SessionActivity? attentionSession;
    private DateTimeOffset attentionUntil;
    public void Peek(SessionActivity? session = null)
    {
        if (settings.Current.Visibility == NotchVisibility.Hidden) return;
        attentionSession = session; attentionUntil = DateTimeOffset.Now.AddSeconds(5);
        SetExpanded(true);
        foldTimer.Interval = TimeSpan.FromSeconds(5); foldTimer.Start();
    }
    public void ApplyVisibility()
    {
        if (lastVisibility != settings.Current.Visibility)
        {
            lastVisibility = settings.Current.Visibility; pinned = false; expanded = false; hovered = null; popup.IsOpen = false;
        }
        if (settings.Current.Visibility == NotchVisibility.Hidden) { popup.IsOpen = false; Hide(); }
        else { if (!IsVisible) Show(); Render(); }
    }
    private void SettingsChanged(object? sender, EventArgs e) => ApplyVisibility();
    private void DisplayChanged(object? sender, PropertyChangedEventArgs e) { if (!closed) Dispatcher.BeginInvoke(() => Render()); }
    private void DisplaysChanged(object? sender, EventArgs e) { if (!closed) Dispatcher.BeginInvoke(() => Render()); }
    internal void TryFold()
    {
        if (accountMenu && popup.IsOpen) return;
        if (IsMouseOver || dragging || trackingMenu || IsKeyboardFocusWithin ||
            popup.Child is UIElement child && (child.IsMouseOver || child.IsKeyboardFocusWithin)) return;
        foldTimer.Stop(); foldTimer.Interval = TimeSpan.FromMilliseconds(450); popup.IsOpen = false; hovered = null;
        if (!pinned && settings.Current.Visibility != NotchVisibility.AlwaysShow) { SetExpanded(false); }
    }
    private string[] VisibleProviderIds() => settings.Current.EnabledProviders.Where(id =>
    {
        var reading = store.AccountDisplay(id).Reading;
        return ProviderAvailability.ShowsInNotch(id, reading, store.ProviderAccountDisplay(id).Account is not null);
    }).ToArray();
    private void Update(object? sender, PropertyChangedEventArgs e) => RefreshReadings();
    internal void RefreshReadings()
    {
        if (closed) return;
        var visibleProviders = VisibleProviderIds();
        if (Expanded && !buttons.Keys.SequenceEqual(visibleProviders, StringComparer.Ordinal))
        {
            var horizontal = viewport?.HorizontalOffset ?? 0; var vertical = viewport?.VerticalOffset ?? 0;
            var focused = buttons.FirstOrDefault(pair => pair.Value.IsKeyboardFocusWithin).Key;
            var preservePopup = popup.IsOpen && (accountMenu || hovered is not null && visibleProviders.Contains(hovered, StringComparer.Ordinal));
            var revealed = ControlsRevealed;
            Render(preservePopup: preservePopup); UpdateLayout();
            viewport?.ScrollToHorizontalOffset(horizontal); viewport?.ScrollToVerticalOffset(vertical);
            if (hovered is not null && !buttons.ContainsKey(hovered)) hovered = null;
            if (focused is not null && buttons.TryGetValue(focused, out var surviving)) surviving.Focus();
            if (revealed) RevealControls();
            if (preservePopup) UpdatePopupAnchor(false);
            return;
        }
        foreach (var ring in rings)
        {
            var display = store.AccountDisplay(ring.ProviderId);
            ring.Reading = ProviderDisplayPolicy.ForNotch(display.Reading, settings.Current, display.RawPlan)?.Evaluated(DateTimeOffset.Now);
            ring.Active = store.Sessions.Any(x => x.Provider == ring.ProviderId && x.State == "busy");
            ring.Waiting = store.Sessions.Any(x => x.Provider == ring.ProviderId && x.State == "waiting");
            ring.Refreshing = store.RefreshingProviders.Contains(ring.ProviderId);
            ring.InvalidateVisual();
            if (buttons.TryGetValue(ring.ProviderId, out var button)) UpdateRingAccessibility(button, ring);
        }
        if (CanRefreshProviderPopup()) RefreshPopup();
    }
    private static void UpdateRingAccessibility(Button button, ProviderRing ring)
    {
        AutomationProperties.SetName(button, (ProviderCatalog.Find(ring.ProviderId)?.Name ?? ring.ProviderId) + "; " + ring.AccessibleReading());
        AutomationProperties.SetHelpText(button, "Show usage details. Activate to refresh or open the session needing attention.");
    }
    internal void Render(bool animateOpening = false, bool preservePopup = false, double openingProgress = 0)
    {
        if (closed) return;
        foldRevision++; movingShape = null;
        rings.Clear(); buttons.Clear();
        if (!preservePopup) { popup.IsOpen = false; Motion.Stop(popupFrame); }
        ResetControls();
        var config = settings.Current; var scale = config.Scale;
        if (!Expanded)
        {
            Width = Vertical ? NotchMetrics.PillDepth * scale : NotchMetrics.PillLength * scale;
            Height = Vertical ? NotchMetrics.PillLength * scale : NotchMetrics.PillDepth * scale;
            Content = new NotchShape { Edge = config.Edge, DesignScale = scale, Fill = Brushes.Black }; Position(); return;
        }
        var screen = SelectedScreen(); var dpi = ScreenScale(screen);
        var available = (Vertical ? screen.WorkingArea.Height : screen.WorkingArea.Width) / dpi - 16;
        var visibleProviders = VisibleProviderIds();
        var fit = NotchMetrics.Fit(config.Edge, visibleProviders.Length, scale, available);
        bodyLength = fit.Length; bodyDepth = fit.Depth;
        var controlsFirst = NotchMetrics.ControlsAtStart(config.Edge, config.ControlsPosition, bodyLength, scale, available, config.Offset);
        bodyStart = controlsFirst ? NotchMetrics.ControlExtent * scale : 0;
        var canvas = new Canvas { Background = null };
        var outerDepth = Math.Max(bodyDepth, (NotchMetrics.Curl + NotchMetrics.OrbArcRadius + NotchMetrics.OrbStroke / 2) * scale);
        Width = Vertical ? outerDepth : bodyLength + NotchMetrics.ControlExtent * scale;
        Height = Vertical ? bodyLength + NotchMetrics.ControlExtent * scale : outerDepth;
        var body = new Grid { Width = Vertical ? bodyDepth : bodyLength, Height = Vertical ? bodyLength : bodyDepth };
        var shape = new NotchShape { Edge = config.Edge, DesignScale = scale, Fill = Brushes.Black };
        movingShape = shape;
        if (animateOpening && Animates) shape.Expansion = Math.Clamp(openingProgress, 0, 1);
        shape.FrameChanged += () => body.Clip = shape.GeometryFor(new Size(body.Width, body.Height));
        body.Clip = shape.GeometryFor(new Size(body.Width, body.Height));
        body.Children.Add(shape);
        var cells = new StackPanel { Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal };
        foreach (var id in visibleProviders)
        {
            var display = store.AccountDisplay(id);
            var ring = new ProviderRing { ProviderId = id, Settings = config, Reading = ProviderDisplayPolicy.ForNotch(display.Reading, config, display.RawPlan)?.Evaluated(DateTimeOffset.Now),
                Active = store.Sessions.Any(x => x.Provider == id && x.State == "busy"),
                Waiting = store.Sessions.Any(x => x.Provider == id && x.State == "waiting"),
                Refreshing = store.RefreshingProviders.Contains(id) };
            rings.Add(ring);
            var button = new Button { Content = ring, Style = (Style)FindResource("NotchButton"),
                Width = Vertical ? NotchMetrics.SideDepth : NotchMetrics.Ring,
                Height = Vertical ? NotchMetrics.CellHeight : NotchMetrics.SideDepth - NotchMetrics.Ring + NotchMetrics.CellHeight,
                Margin = Vertical ? new Thickness(0, 0, 0, NotchMetrics.CellGap) : new Thickness(0, 0, NotchMetrics.CellGap, 0) };
            if (animateOpening && Animates)
            {
                button.Opacity = 0;
                var slide = 28 * NotchMetrics.Unit;
                button.RenderTransform = new TranslateTransform(config.Edge == NotchEdge.Left ? -slide : config.Edge == NotchEdge.Right ? slide : 0,
                    config.Edge == NotchEdge.Top ? -slide : config.Edge == NotchEdge.Bottom ? slide : 0);
            }
            buttons[id] = button;
            UpdateRingAccessibility(button, ring);
            AutomationProperties.SetAutomationId(button, "notch.provider." + id);
            button.Click += async (_, _) =>
            {
                if (attentionSession is { } session && session.Provider == id && DateTimeOffset.Now <= attentionUntil)
                {
                    attentionSession = null; popup.IsOpen = false;
                    if (!SessionFocus.Activate(session)) openSettings("sessions:" + id);
                }
                else await store.RefreshProviderAsync(id).ConfigureAwait(true);
            };
            button.MouseEnter += (_, _) => OpenProvider(id);
            button.MouseLeave += (_, _) => hoverClear.Start();
            button.LostKeyboardFocus += (_, _) => hoverClear.Start();
            button.GotKeyboardFocus += (_, _) => OpenProvider(id);
            cells.Children.Add(button);
        }
        if (cells.Children.Count > 0) ((FrameworkElement)cells.Children[^1]).Margin = new Thickness(0);
        else
        {
            var add = Control("\uE710", "Add provider", () => openSettings("providers"));
            add.Width = NotchMetrics.Ring; add.Height = NotchMetrics.CellHeight; cells.Children.Add(add);
        }
        viewport = new ScrollViewer { Content = cells, VerticalScrollBarVisibility = Vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = Vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden,
            CanContentScroll = false, Focusable = false, PanningMode = PanningMode.None,
            LayoutTransform = new ScaleTransform(scale, scale),
            Margin = Vertical ? new Thickness(0, (NotchMetrics.Curl + NotchMetrics.PadStart) * scale, 0, (NotchMetrics.Curl + NotchMetrics.PadEnd) * scale)
                : new Thickness((NotchMetrics.Curl + NotchMetrics.StartPadding(config.Edge)) * scale, 0, (NotchMetrics.Curl + NotchMetrics.EndPadding(config.Edge)) * scale, 0) };
        viewport.PreviewMouseWheel += (_, e) =>
        {
            if (Vertical) viewport.ScrollToVerticalOffset(viewport.VerticalOffset - e.Delta / 2d);
            else viewport.ScrollToHorizontalOffset(viewport.HorizontalOffset - e.Delta / 2d);
            popup.IsOpen = false; e.Handled = true;
        };
        body.Children.Add(viewport); canvas.Children.Add(body);
        if (Vertical) Canvas.SetTop(body, bodyStart); else Canvas.SetLeft(body, bodyStart);
        if (config.Edge == NotchEdge.Right) Canvas.SetLeft(body, outerDepth - bodyDepth);
        if (config.Edge == NotchEdge.Bottom) Canvas.SetTop(body, outerDepth - bodyDepth);
        if (visibleProviders.Length > 0) RenderControls(canvas, controlsFirst);
        var menu = new ContextMenu();
        menu.Opened += (_, _) => { trackingMenu = true; foldTimer.Stop(); };
        menu.Closed += (_, _) => { trackingMenu = false; foldTimer.Start(); };
        var keep = new MenuItem { Header = "Keep open", IsCheckable = true, IsChecked = pinned };
        keep.Click += (_, _) => { pinned = keep.IsChecked; foldTimer.Start(); }; menu.Items.Add(keep);
        AddMenu(menu, "Refresh", () => _ = store.RefreshAsync(true));
        AddMenu(menu, "Recentre", () => settings.Save(config with { Offset = 0 }));
        AddMenu(menu, "Settings…", () => openSettings(null));
        AddMenu(menu, "Hide notch", () => settings.Save(config with { Visibility = NotchVisibility.Hidden }));
        canvas.ContextMenu = menu; Content = canvas; Position();
        if (animateOpening) { if (settingsControl is not null && Animates) settingsControl.Opacity = 0; AnimateFold(); }
    }
    private Button Control(string glyph, string label, Action action)
    {
        var button = new Button { Style = (Style)FindResource("IconButton"), Content = new TextBlock { Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20, Foreground = Brushes.White },
            Margin = new Thickness(0, 2, 0, 2), ToolTip = label };
        AutomationProperties.SetName(button, label);
        void ClearProviderCard() { if (!accountMenu) { hoverClear.Stop(); popup.IsOpen = false; hovered = null; } }
        button.MouseEnter += (_, _) => ClearProviderCard();
        button.GotKeyboardFocus += (_, _) => ClearProviderCard();
        button.Click += (_, _) => action(); return button;
    }
    private static void AddMenu(ContextMenu menu, string label, Action action)
    {
        var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item);
    }
    internal void OpenProvider(string id)
    {
        if (accountMenu && popup.IsOpen || !buttons.ContainsKey(id)) return;
        var transition = !popup.IsOpen || hovered != id;
        if (transition) sessionExpansion.Expanded = false;
        hoverClear.Stop(); popup.StaysOpen = true; hovered = id; RefreshPopup(); RevealPopup(transition); foldTimer.Stop();
    }
    internal void DismissProviderCard()
    {
        if (accountMenu) { hoverClear.Stop(); return; }
        if (hovered is not null && buttons.TryGetValue(hovered, out var target) && (target.IsMouseOver || target.IsKeyboardFocusWithin)) return;
        if (popup.IsOpen && popup.Child is UIElement child && (child.IsMouseOver || child.IsKeyboardFocusWithin)) return;
        hoverClear.Stop(); hovered = null; FadeProviderPopup();
    }
    private readonly SessionExpansionState sessionExpansion = new();
    internal void RefreshPopupClock() { if (CanRefreshProviderPopup()) RefreshPopup(); }
    private bool CanRefreshProviderPopup()
    {
        if (accountMenu || !popup.IsOpen || popup.Child is not UIElement child) return false;
        if (!child.IsKeyboardFocusWithin) return true;
        if (Mouse.LeftButton == MouseButtonState.Pressed || Keyboard.IsKeyDown(Key.Space) || Keyboard.IsKeyDown(Key.Return)) return false;
        var id = Keyboard.FocusedElement is DependencyObject focused ? AutomationProperties.GetAutomationId(focused) : "";
        return id.StartsWith("notch.session.", StringComparison.Ordinal) || id.StartsWith("notch.sessions.", StringComparison.Ordinal);
    }
    private void RefreshPopup()
    {
        if (hovered is null || !buttons.ContainsKey(hovered)) return;
        var scrollOffset = FindScroll(popup.Child)?.VerticalOffset ?? 0;
        var focusedId = popup.Child is UIElement child && child.IsKeyboardFocusWithin && Keyboard.FocusedElement is DependencyObject focused
            ? AutomationProperties.GetAutomationId(focused) : null;
        var screen = SelectedScreen();
        var card = NotchPopover.Create(hovered, store, settings.Current, page => { popup.IsOpen = false; openSettings(page); }, screen.WorkingArea.Height / ScreenScale(screen), sessionExpansion);
        card.Loaded += (_, _) =>
        {
            if (!ReferenceEquals(popupFrame.Child, card)) return;
            if (!string.IsNullOrEmpty(focusedId))
                (FindPopupControl(card, focusedId) ?? FindPopupControl(card, sessionExpansion.Expanded ? "notch.sessions.showLess" : "notch.sessions.showAll")
                    ?? buttons.GetValueOrDefault(hovered ?? ""))?.Focus();
            FindScroll(card)?.ScrollToVerticalOffset(scrollOffset);
        };
        AttachPopup(card); popupFrame.Child = card; UpdatePopupAnchor(popup.IsOpen);
    }
    private static Control? FindPopupControl(DependencyObject root, string id)
    {
        if (root is Control control && AutomationProperties.GetAutomationId(control) == id) return control;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindPopupControl(VisualTreeHelper.GetChild(root, index), id) is { } found) return found;
        return null;
    }
    private static ScrollViewer? FindScroll(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private void AttachPopup(FrameworkElement child)
    {
        child.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DismissPopupFromKeyboard(); e.Handled = true; } };
        child.MouseEnter += (_, _) => { foldTimer.Stop(); hoverClear.Stop(); };
        child.MouseLeave += (_, _) => { foldTimer.Start(); if (!accountMenu) hoverClear.Start(); };
        child.LostKeyboardFocus += (_, _) => foldTimer.Start();
    }
    private void DismissPopupFromKeyboard()
    {
        var returnToAccount = accountMenu && popup.IsOpen;
        popup.IsOpen = false; hovered = null;
        if (returnToAccount && accountControl is { IsVisible: true, IsEnabled: true } account) account.Focus();
        else Keyboard.ClearFocus();
        foldTimer.Start();
    }
    internal void OpenAccounts()
    {
        var openedFromKeyboard = accountControl?.IsKeyboardFocusWithin == true;
        popup.IsOpen = false; accountMenu = true; popup.StaysOpen = false; hoverClear.Stop(); foldTimer.Stop();
        RevealControls();
        hovered = null; var list = new StackPanel { Margin = new Thickness(12) };
        list.Children.Add(NotchPopover.Text("Accounts", 14, Brushes.White, FontWeights.SemiBold));
        foreach (var id in settings.Current.EnabledProviders.Where(x => x != "ollama-local"))
        {
            var display = store.AccountDisplay(id);
            var reading = display.Reading?.Evaluated(DateTimeOffset.Now);
            var name = ProviderCatalog.Find(id)?.Name ?? id;
            var account = display.Label;
            var state = reading?.State is ReadingState.Ready or ReadingState.Partial or ReadingState.Stale ? "Connected" : "Connect account";
            var button = Ui.Button(name, () => { popup.IsOpen = false; openSettings(id is "codex" or "claude" ? id + "-accounts" : id); });
            button.BorderThickness = new Thickness(0); button.Background = Ui.Brush("#202020"); button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetAutomationId(button, "notch.account." + id);
            var row = new DockPanel();
            var logo = new ProviderMark { ProviderId = id, Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(logo, Dock.Left); row.Children.Add(logo);
            var labels = new StackPanel();
            labels.Children.Add(NotchPopover.Text(name + (reading?.Plan is { Length: > 0 } plan ? " · " + plan : ""), 12, Brushes.White, FontWeights.SemiBold));
            var identity = NotchPopover.Text(account ?? state, 10.5, Ui.Brush("#D4D4D4")); identity.TextWrapping = TextWrapping.NoWrap; identity.TextTrimming = TextTrimming.CharacterEllipsis;
            identity.ToolTip = account is null ? state : "CLI login file · " + account;
            labels.Children.Add(identity); row.Children.Add(labels); button.Content = row; list.Children.Add(button);
        }
        if (list.Children.Count == 1) list.Children.Add(Ui.Button("Manage Providers", () => { popup.IsOpen = false; openSettings("providers"); }));
        var frame = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(16), Width = 280,
            Child = new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        // The notch remains black independently of the system Settings theme.
        frame.Resources["PrimaryText"] = Brushes.White;
        frame.Resources["SecondaryText"] = Ui.Brush("#D4D4D4");
        frame.Resources["ControlHover"] = Ui.Brush("#343434");
        frame.Resources["ControlBackground"] = Ui.Brush("#202020");
        frame.Resources["DividerBrush"] = Ui.Brush("#404040");
        KeyboardNavigation.SetTabNavigation(frame, KeyboardNavigationMode.Cycle);
        // Pointer invocation keeps the editor active; keyboard invocation enters
        // the menu after its native popup has been attached and measured.
        if (openedFromKeyboard)
            frame.Loaded += (_, _) =>
            {
                if (accountMenu && popup.IsOpen && ReferenceEquals(popupFrame.Child, frame))
                    list.Children.OfType<Button>().FirstOrDefault()?.Focus();
            };
        AttachPopup(frame); popupFrame.Child = frame; UpdatePopupAnchor(false); RevealPopup(true);
    }
    private CustomPopupPlacement[] PlacePopup(Size popupSize, Size targetSize, Point offset)
    {
        var center = PopupAnchor;
        // WPF supplies custom-placement sizes in device pixels. The native
        // target origin is then added by Popup, so convert every relative DIP
        // measurement before mixing it with popupSize/targetSize.
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        center = transform.Transform(center);
        var gapX = NotchMetrics.TailGap * transform.M11;
        var gapY = NotchMetrics.TailGap * transform.M22;
        var point = settings.Current.Edge switch
        {
            NotchEdge.Right => new Point(-popupSize.Width - gapX, center.Y - popupSize.Height / 2),
            NotchEdge.Left => new Point(targetSize.Width + gapX, center.Y - popupSize.Height / 2),
            NotchEdge.Top => new Point(center.X - popupSize.Width / 2, targetSize.Height + gapY),
            _ => new Point(center.X - popupSize.Width / 2, -popupSize.Height - gapY)
        };
        // WPF applies Horizontal/VerticalOffset to the target rectangle before invoking
        // custom placement. These offsets also trigger each animation-frame reposition;
        // subtract their device-space contribution so the animated anchor is applied once.
        point -= transform.Transform(new Vector(offset.X, offset.Y));
        return [new CustomPopupPlacement(point, Vertical ? PopupPrimaryAxis.Vertical : PopupPrimaryAxis.Horizontal)];
    }
    private Screen SelectedScreen() => Screen.AllScreens.FirstOrDefault(x => x.DeviceName == settings.Current.Display) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
    private void Position(double? temporaryOffset = null)
    {
        var screen = SelectedScreen(); var area = screen.WorkingArea; var dpi = ScreenScale(screen);
        var position = NotchGeometry.Place(new ScreenArea(area.X, area.Y, area.Width, area.Height), Width * dpi, Height * dpi,
            settings.Current.Edge, ((temporaryOffset ?? settings.Current.Offset) + (Content is Canvas
                ? (Vertical ? Height : Width) / 2 - bodyStart - bodyLength / 2 : 0)) * dpi);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetWindowPos(handle, new IntPtr(-1), (int)position.X, (int)position.Y, (int)Math.Ceiling(Width * dpi), (int)Math.Ceiling(Height * dpi), 0x10);
    }
    private static double ScreenScale(Screen screen)
    {
        var point = new NativePoint { X = screen.Bounds.X + screen.Bounds.Width / 2, Y = screen.Bounds.Y + screen.Bounds.Height / 2 };
        return GetDpiForMonitor(MonitorFromPoint(point, 2), 0, out var x, out _) == 0 ? Math.Max(96, x) / 96d : 1;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
#pragma warning disable SYSLIB1054
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
#pragma warning restore SYSLIB1054
}
internal static class DrawingPointExtensions
{
    internal static Point ToPoint(this System.Drawing.Point point) => new(point.X, point.Y);
}
