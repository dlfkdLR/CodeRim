using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
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

    private void ConfigureShell(Grid layout, GridSplitter splitter)
    {
        sidebarColumn = layout.ColumnDefinitions[0]; sidebarSplitter = splitter;
        sidebarColumn.Width = new GridLength(sidebarWidth); sidebarColumn.MinWidth = 208; sidebarColumn.MaxWidth = 268;
        layout.Children.Remove(sidebar);
        sidebar.Margin = new Thickness(10, 8, 10, 8); sidebar.Padding = new Thickness(0); sidebar.BorderThickness = new Thickness(0); sidebar.Background = Brushes.Transparent;
        var surface = new Border { CornerRadius = new CornerRadius(18), Margin = new Thickness(8, 8, 0, 8), Child = sidebar };
        surface.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); sidebarSurface = surface; layout.Children.Insert(0, surface);

        // Keep native drag, resize, double-click maximize and system-menu behavior
        // while matching the reference app's unified toolbar and window controls.
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 48, GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness, CornerRadius = new CornerRadius(12), UseAeroCaptionButtons = false });
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) }); root.RowDefinitions.Add(new RowDefinition());
        var toolbar = new Grid { Margin = new Thickness(18, 0, 14, 0) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) }); toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var windowButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        windowButtons.Children.Add(WindowButton("Close", "WindowCloseBrush", "×", () => SystemCommands.CloseWindow(this)));
        windowButtons.Children.Add(WindowButton("Minimize", "WindowMinimizeBrush", "−", () => SystemCommands.MinimizeWindow(this)));
        var maximize = WindowButton("Maximize", "WindowMaximizeBrush", "+", () => { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); });
        StateChanged += (_, _) => { var name = WindowState == WindowState.Maximized ? "Restore" : "Maximize"; AutomationProperties.SetName(maximize, name); maximize.ToolTip = name; };
        windowButtons.Children.Add(maximize); toolbar.Children.Add(windowButtons);
        sectionTitle.Margin = new Thickness(0); sectionTitle.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(sectionTitle, 1); toolbar.Children.Add(sectionTitle);
        sidebarToggle = Ui.Button("", ToggleSidebar); sidebarToggle.Width = sidebarToggle.Height = 32; sidebarToggle.Margin = new Thickness(0); sidebarToggle.Padding = new Thickness(8);
        sidebarToggle.Background = Brushes.Transparent; sidebarToggle.BorderThickness = new Thickness(0);
        sidebarToggle.Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M1,1 H15 V15 H1 Z M6,1 V15 M3,4 H4 M3,7 H4 M3,10 H4"),
            Width = 16, Height = 16, StrokeThickness = 1.2, Stretch = Stretch.Uniform };
        ((System.Windows.Shapes.Path)sidebarToggle.Content).SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText");
        AutomationProperties.SetAutomationId(sidebarToggle, "settings.sidebar.toggle"); UpdateSidebarButton();
        WindowChrome.SetIsHitTestVisibleInChrome(sidebarToggle, true); Grid.SetColumn(sidebarToggle, 2); toolbar.Children.Add(sidebarToggle);
        root.Children.Add(toolbar); Grid.SetRow(layout, 1); root.Children.Add(layout); Content = root;
        Width = 980; Height = 680 + 48; MinWidth = 840; MinHeight = 560 + 48;
        RestoreSettingsFrame();
        Loaded += (_, _) =>
        {
            var caption = PointToScreen(new Point(100, 24));
            if (!System.Windows.Forms.Screen.AllScreens.Any(screen => screen.WorkingArea.Contains((int)caption.X, (int)caption.Y)))
            { Left = SystemParameters.WorkArea.Left + Math.Max(0, (SystemParameters.WorkArea.Width - Width) / 2); Top = SystemParameters.WorkArea.Top; }
        };
        Closing += (_, _) => SaveSettingsFrame();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)) { ToggleSidebar(); e.Handled = true; }
        };
    }

    private static System.Windows.Controls.Button WindowButton(string name, string color, string symbol, Action action)
    {
        var glyph = new TextBlock { Text = symbol, FontSize = 12, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "WindowButtonText"); glyph.SetResourceReference(OpacityProperty, "WindowButtonSymbolOpacity");
        var button = Ui.Button("", action); button.Width = button.Height = button.MinHeight = 14;
        button.Padding = new Thickness(0); button.Margin = new Thickness(0, 0, 9, 0); button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
        var grid = new Grid(); var circle = new System.Windows.Shapes.Ellipse { StrokeThickness = 1 };
        circle.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, color); circle.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "WindowButtonOutline");
        grid.Children.Add(circle); grid.Children.Add(glyph); button.Content = grid;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Stretch;
        void UpdateGlyph() { if (button.IsMouseOver || button.IsKeyboardFocusWithin) glyph.Opacity = 1; else glyph.SetResourceReference(OpacityProperty, "WindowButtonSymbolOpacity"); }
        button.MouseEnter += (_, _) => UpdateGlyph(); button.MouseLeave += (_, _) => UpdateGlyph(); button.IsKeyboardFocusWithinChanged += (_, _) => UpdateGlyph();
        button.ToolTip = name; AutomationProperties.SetName(button, name); AutomationProperties.SetAutomationId(button, "settings.window." + name.ToLowerInvariant());
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
