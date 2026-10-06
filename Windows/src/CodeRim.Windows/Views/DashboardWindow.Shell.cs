using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class DashboardWindow
{
    private readonly TextBlock sectionTitle = Ui.Text("Usage", 14, weight: FontWeights.SemiBold);
    private ColumnDefinition? sidebarColumn;
    private FrameworkElement? sidebarSurface;
    private GridSplitter? sidebarSplitter;
    private System.Windows.Controls.Button? sidebarToggle;
    private double sidebarWidth = 224;
    private bool sidebarHidden;
    private bool fittingFrame;
    private bool shellClosed;
    private HwndSource? frameSource;
    private string? frameMonitor;
    private bool frameDragging;
    private bool frameFitPending;

    private void ConfigureShell(Grid layout, GridSplitter splitter)
    {
        sidebarColumn = layout.ColumnDefinitions[0]; sidebarSplitter = splitter;
        sidebarColumn.Width = new GridLength(sidebarWidth); sidebarColumn.MinWidth = 208; sidebarColumn.MaxWidth = 268;
        layout.Children.Remove(sidebar);
        sidebar.Margin = new Thickness(10, 8, 10, 8); sidebar.Padding = new Thickness(0); sidebar.BorderThickness = new Thickness(0); sidebar.Background = Brushes.Transparent;
        var surface = new Border { Child = sidebar };
        surface.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); sidebarSurface = surface; layout.Children.Insert(0, surface);

        // Keep native drag, resize, double-click maximize and system-menu behavior
        // with Windows-style caption controls on the right and the shared page toolbar.
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 48, GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness, CornerRadius = new CornerRadius(12), UseAeroCaptionButtons = false });
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) }); root.RowDefinitions.Add(new RowDefinition());
        var toolbar = new Grid();
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var windowButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        windowButtons.Children.Add(WindowButton("Minimize", "M1,7 H11", () => SystemCommands.MinimizeWindow(this)));
        var maximize = WindowButton("Maximize", "M1,1 H11 V11 H1 Z", () => { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); });
        StateChanged += (_, _) =>
        {
            var restored = WindowState == WindowState.Maximized;
            var name = restored ? "Restore" : "Maximize"; AutomationProperties.SetName(maximize, name); maximize.ToolTip = name;
            ((System.Windows.Shapes.Path)maximize.Content).Data = Geometry.Parse(restored ? "M3,1 H11 V9 M1,3 H9 V11 H1 Z" : "M1,1 H11 V11 H1 Z");
        };
        windowButtons.Children.Add(maximize);
        windowButtons.Children.Add(WindowButton("Close", "M1,1 L11,11 M11,1 L1,11", () => SystemCommands.CloseWindow(this), close: true));
        Grid.SetColumn(windowButtons, 2); toolbar.Children.Add(windowButtons);
        sectionTitle.Margin = new Thickness(0); sectionTitle.VerticalAlignment = VerticalAlignment.Center;
        sectionTitle.TextWrapping = TextWrapping.NoWrap; sectionTitle.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(sectionTitle, 1); toolbar.Children.Add(sectionTitle);
        sidebarToggle = Ui.Button("", ToggleSidebar); sidebarToggle.Width = sidebarToggle.Height = 32; sidebarToggle.Margin = new Thickness(12, 0, 12, 0); sidebarToggle.Padding = new Thickness(8);
        sidebarToggle.Background = Brushes.Transparent; sidebarToggle.BorderThickness = new Thickness(0);
        sidebarToggle.Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M1,1 H15 V15 H1 Z M6,1 V15 M3,4 H4 M3,7 H4 M3,10 H4"),
            Width = 16, Height = 16, StrokeThickness = 1.2, Stretch = Stretch.Uniform };
        ((System.Windows.Shapes.Path)sidebarToggle.Content).SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText");
        AutomationProperties.SetAutomationId(sidebarToggle, "settings.sidebar.toggle"); UpdateSidebarButton();
        WindowChrome.SetIsHitTestVisibleInChrome(sidebarToggle, true); toolbar.Children.Add(sidebarToggle);
        root.Children.Add(toolbar); Grid.SetRow(layout, 1); root.Children.Add(layout); Content = root;
        Width = 980; Height = 680 + 48; MinWidth = 840; MinHeight = 560 + 48;
        RestoreSettingsFrame();
        SourceInitialized += (_, _) =>
        {
            frameSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            frameSource?.AddHook(SettingsFrameMessage);
            Services.WindowCorners.Round(new WindowInteropHelper(this).Handle);
        };
        Loaded += (_, _) => FitSettingsFrame();
        StateChanged += (_, _) => QueueFitSettingsFrame();
        LocationChanged += (_, _) =>
        {
            if (IsLoaded && !shellClosed && !frameDragging && frameSource is { } source
                && System.Windows.Forms.Screen.FromHandle(source.Handle).DeviceName != frameMonitor)
                QueueFitSettingsFrame(); // Keyboard/programmatic monitor moves.
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += SettingsDisplaysChanged;
        SystemParameters.StaticPropertyChanged += SettingsWorkAreaChanged;
        Closed += (_, _) =>
        {
            shellClosed = true;
            frameSource?.RemoveHook(SettingsFrameMessage); frameSource = null;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SettingsDisplaysChanged;
            SystemParameters.StaticPropertyChanged -= SettingsWorkAreaChanged;
        };
        Closing += (_, _) => SaveSettingsFrame();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)) { ToggleSidebar(); e.Handled = true; }
        };
    }

    private IntPtr SettingsFrameMessage(IntPtr handle, int message, IntPtr word, IntPtr parameter, ref bool handled)
    {
        // Equal-DPI monitor moves do not raise DpiChanged. Fit after dragging
        // finishes, never while the user is positioning a window on a monitor.
        if (message == 0x0231) frameDragging = true; // WM_ENTERSIZEMOVE
        if (message == 0x0232) // WM_EXITSIZEMOVE
        {
            frameDragging = false;
            if (frameFitPending || System.Windows.Forms.Screen.FromHandle(handle).DeviceName != frameMonitor)
                QueueFitSettingsFrame();
        }
        return IntPtr.Zero;
    }

    private void SettingsDisplaysChanged(object? sender, EventArgs args) => QueueFitSettingsFrame();
    private void SettingsWorkAreaChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.WorkArea)) QueueFitSettingsFrame();
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi); QueueFitSettingsFrame();
    }
    private void QueueFitSettingsFrame()
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(FitSettingsFrame);
    }
    private void FitSettingsFrame()
    {
        if (shellClosed || !IsLoaded || fittingFrame || WindowState != WindowState.Normal) return;
        if (frameDragging) { frameFitPending = true; return; }
        frameFitPending = false;
        fittingFrame = true;
        try
        {
            var monitor = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var area = monitor.WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            if (area.Width <= 0 || area.Height <= 0) return;
            var width = area.Width / dpi.DpiScaleX; var height = area.Height / dpi.DpiScaleY;
            // A smaller or higher-DPI display must not trap the caption or footer
            // outside its usable area. Larger screens retain the normal minimum.
            MinWidth = Math.Min(840, width); MinHeight = Math.Min(608, height);
            Width = Math.Clamp(ActualWidth, MinWidth, width); Height = Math.Clamp(ActualHeight, MinHeight, height);
            UpdateLayout();
            var origin = PointToScreen(new Point());
            var left = Math.Clamp(origin.X, area.Left, Math.Max(area.Left, area.Right - ActualWidth * dpi.DpiScaleX));
            var top = Math.Clamp(origin.Y, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight * dpi.DpiScaleY));
            // Apply a device-pixel delta at this HWND's DPI; dividing global
            // monitor coordinates by the primary display's DPI misplaces it.
            Left += (left - origin.X) / dpi.DpiScaleX; Top += (top - origin.Y) / dpi.DpiScaleY;
            frameMonitor = monitor.DeviceName;
        }
        finally { fittingFrame = false; }
    }

    private static System.Windows.Controls.Button WindowButton(string name, string geometry, Action action, bool close = false)
    {
        var button = new System.Windows.Controls.Button(); button.SetResourceReference(StyleProperty, close ? "WindowCloseButton" : "WindowCaptionButton");
        button.Click += (_, _) => action();
        button.Margin = new Thickness(0);
        var glyph = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = 12, Height = 12,
            Stretch = Stretch.Uniform, StrokeThickness = 1, IsHitTestVisible = false };
        glyph.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = button });
        button.Content = glyph; button.ToolTip = name; AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, "settings.window." + name.ToLowerInvariant());
        WindowChrome.SetIsHitTestVisibleInChrome(button, true); return button;
    }

    private void ToggleSidebar()
    {
        if (sidebarColumn is null || sidebarSurface is null || sidebarSplitter is null) return;
        if (!sidebarHidden) sidebarWidth = sidebarColumn.ActualWidth;
        sidebarHidden = !sidebarHidden;
        sidebarColumn.MinWidth = sidebarHidden ? 0 : 208; sidebarColumn.MaxWidth = sidebarHidden ? 0 : 268;
        sidebarColumn.Width = new GridLength(sidebarHidden ? 0 : Math.Clamp(sidebarWidth, 208, 268));
        sidebarSurface.Visibility = sidebarSplitter.Visibility = sidebarHidden ? Visibility.Collapsed : Visibility.Visible;
        UpdateSidebarButton();
    }

    private void UpdateSidebarButton()
    {
        if (sidebarToggle is null) return;
        var label = sidebarHidden ? "Show Sidebar" : "Hide Sidebar";
        AutomationProperties.SetName(sidebarToggle, label); sidebarToggle.ToolTip = label + " (Ctrl+Alt+S)";
    }

    private void UpdateSectionTitle()
    {
        Title = sectionTitle.Text = page switch { "general" => "General", "usage" => "Usage", "providers" => "Providers", "notch" => "Notch", "diagnostics" => "Diagnostics", "about" => "Information", _ => "Providers" };
    }

    private sealed record SettingsFrame(double Left, double Top, double Width, double Height, double SidebarWidth);
    private static string FramePath => Path.Combine(CompanionFile.DataDirectory, "settings-window.json");
    private void RestoreSettingsFrame()
    {
        try
        {
            if (!File.Exists(FramePath) || new FileInfo(FramePath).Length > 4096) return;
            var frame = JsonSerializer.Deserialize<SettingsFrame>(File.ReadAllText(FramePath));
            if (frame is null || !new[] { frame.Left, frame.Top, frame.Width, frame.Height, frame.SidebarWidth }.All(double.IsFinite)) return;
            Width = Math.Clamp(frame.Width, MinWidth, 4096); Height = Math.Clamp(frame.Height, MinHeight, 4096);
            sidebarWidth = Math.Clamp(frame.SidebarWidth, 208, 268); if (sidebarColumn is not null) sidebarColumn.Width = new GridLength(sidebarWidth);
            // WPF's virtual desktop coordinates are device-independent. Retain a
            // recoverable caption on the virtual desktop after monitor changes.
            var virtualDesktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (virtualDesktop.Contains(new Point(frame.Left + 100, frame.Top + 24))) { Left = frame.Left; Top = frame.Top; WindowStartupLocation = WindowStartupLocation.Manual; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void SaveSettingsFrame()
    {
        try
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (bounds.IsEmpty) return;
            var frame = new SettingsFrame(bounds.Left, bounds.Top, bounds.Width, bounds.Height, sidebarHidden ? sidebarWidth : sidebarColumn?.ActualWidth ?? sidebarWidth);
            File.WriteAllText(FramePath + ".new", JsonSerializer.Serialize(frame)); File.Move(FramePath + ".new", FramePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
