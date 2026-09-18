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

internal sealed class NotchWindow : Window
{
    private readonly DashboardStore store;
    private readonly AppSettingsStore settings;
    private readonly Action<string?> openSettings;
    private readonly DispatcherTimer foldTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer animation = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool expanded;
    private readonly List<ProviderRing> rings = [];
    public NotchWindow(DashboardStore store, AppSettingsStore settings, Action<string?> openSettings)
    {
        this.store = store; this.settings = settings; this.openSettings = openSettings;
        Title = "CodeRim notch"; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; ShowActivated = false;
        AutomationProperties.SetName(this, "CodeRim provider usage notch");
        store.PropertyChanged += Update; settings.SettingsChanged += SettingsChanged;
        MouseEnter += (_, _) => { foldTimer.Stop(); if (!expanded) { expanded = true; Render(); } };
        MouseLeave += (_, _) => foldTimer.Start();
        // A focused button can postpone folding. Recheck after focus leaves,
        // including activation moving to another application.
        LostKeyboardFocus += (_, _) => foldTimer.Start();
        Deactivated += (_, _) => foldTimer.Start();
        foldTimer.Tick += (_, _) => { foldTimer.Stop(); if (!IsMouseOver && !IsKeyboardFocusWithin) { expanded = false; Render(); } };
        animation.Tick += (_, _) => { foreach (var ring in rings) { ring.Phase = DateTimeOffset.Now.ToUnixTimeMilliseconds() % 3000 / 3000d; ring.InvalidateVisual(); } };
        SourceInitialized += (_, _) => Render();
        Closed += (_, _) => { foldTimer.Stop(); animation.Stop(); store.PropertyChanged -= Update; settings.SettingsChanged -= SettingsChanged; };
        SystemParameters.StaticPropertyChanged += DisplayChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= DisplayChanged;
    }
    public void ApplyVisibility()
    {
        if (settings.Current.Visibility == NotchVisibility.Hidden) { animation.Stop(); Hide(); }
        else { if (!IsVisible) Show(); Render(); }
    }
    private void SettingsChanged(object? sender, EventArgs e) => ApplyVisibility();
    private void DisplayChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Render);
    private void Update(object? sender, PropertyChangedEventArgs e)
    {
        foreach (var ring in rings)
        {
            ring.Reading = store.Readings.GetValueOrDefault(ring.ProviderId);
            ring.Active = store.Sessions.Any(x => x.Provider == ring.ProviderId && x.State == "busy");
            ring.InvalidateVisual();
        }
    }
    private void Render()
    {
        rings.Clear(); animation.Stop();
        var config = settings.Current;
        var open = expanded || config.Visibility == NotchVisibility.AlwaysShow;
        var vertical = config.Edge is NotchEdge.Left or NotchEdge.Right;
        var scale = config.Scale;
        if (!open)
        {
            Width = vertical ? 12 : 80; Height = vertical ? 80 : 12;
            Content = new NotchShape { Edge = config.Edge, Fill = Brushes.Black, ToolTip = "CodeRim — hover to see usage" };
            Position(); return;
        }
        var panel = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, Margin = new Thickness(8) };
        foreach (var id in config.EnabledProviders)
        {
            var ring = new ProviderRing { ProviderId = id, Settings = config, Reading = store.Readings.GetValueOrDefault(id),
                Active = store.Sessions.Any(x => x.Provider == id && x.State == "busy") };
            rings.Add(ring);
            var button = new Button { Content = ring, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                Cursor = Cursors.Hand };
            AutomationProperties.SetName(button, (ProviderCatalog.Find(id)?.Name ?? id) + " usage; open provider settings");
            button.Click += (_, _) => openSettings(id);
            button.ToolTipOpening += (_, _) => button.ToolTip = Tooltip(id);
            button.ToolTip = "Usage"; ToolTipService.SetInitialShowDelay(button, 120); ToolTipService.SetShowDuration(button, 120000);
            ToolTipService.SetPlacement(button, config.Edge switch { NotchEdge.Right => PlacementMode.Left, NotchEdge.Left => PlacementMode.Right, NotchEdge.Top => PlacementMode.Bottom, _ => PlacementMode.Top });
            panel.Children.Add(button);
        }
        var controls = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal };
        controls.Children.Add(Ui.Button("⚙", () => openSettings(null)));
        controls.Children.Add(Ui.AsyncButton("↻", () => store.RefreshAsync(true)));
        panel.Children.Add(controls);
        if (config.EnabledProviders.Length == 0) panel.Children.Insert(0, Ui.Button("+", () => openSettings("providers")));
        var screen = SelectedScreen(); var maxAlong = (vertical ? screen.WorkingArea.Height : screen.WorkingArea.Width) * 0.8 / ScreenScale(screen);
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto };
        var shell = new Grid { LayoutTransform = new ScaleTransform(scale, scale) };
        shell.Children.Add(new NotchShape { Edge = config.Edge, Fill = Brushes.Black });
        scroll.Margin = vertical ? new Thickness(0, 14, 0, 14) : new Thickness(14, 0, 14, 0); shell.Children.Add(scroll);
        Width = vertical ? 82 * scale : Math.Min(maxAlong, (config.EnabledProviders.Length * 64 + 116) * scale);
        Height = vertical ? Math.Min(maxAlong, (config.EnabledProviders.Length * 76 + 100) * scale) : 92 * scale;
        var context = new ContextMenu();
        var hide = new MenuItem { Header = "Hide notch" }; hide.Click += (_, _) => settings.Save(config with { Visibility = NotchVisibility.Hidden }); context.Items.Add(hide);
        shell.ContextMenu = context; Content = shell; Position();
        if (config.RingColor == RingColorMode.Gradient && config.AnimateGradient && !config.ReduceMotion && SystemParameters.ClientAreaAnimation) animation.Start();
    }
    private Border Tooltip(string id)
    {
        var content = Ui.Stack(16); content.MinWidth = 250; content.MaxWidth = 350;
        content.Children.Add(Ui.Text(ProviderCatalog.Find(id)?.Name ?? id, 17, weight: FontWeights.SemiBold));
        if (store.Readings.TryGetValue(id, out var reading))
        {
            if (reading.Plan is { } plan) content.Children.Add(Ui.Text(plan, color: "#BBBBBB"));
            foreach (var window in reading.Windows)
            {
                content.Children.Add(Ui.Text(window.Name, 12));
                if (window.UsedPercent is { } percent)
                {
                    content.Children.Add(new ProgressBar { Value = Math.Clamp(percent, 0, 100), Height = 4, Foreground = Ui.Brush(NotchGeometry.BandColor(percent)), Background = Ui.Brush("#303030"), Margin = new Thickness(0, 3, 0, 6) });
                    content.Children.Add(Ui.Text($"{percent:0.#}% used · {Math.Max(0, 100 - percent):0.#}% remaining", 12, "#C7C7CC"));
                }
                if (window.DisplayValue is { } display) content.Children.Add(Ui.Text(display, 12));
                if (window.UsedCount is { } count) content.Children.Add(Ui.Text($"{count:N0} {window.Unit ?? "units"} used", 12));
                if (window.RemainingCount is { } remaining) content.Children.Add(Ui.Text($"{remaining:N0} {window.Unit ?? "units"} left", 12));
                if (window.ResetsAt is { } reset) content.Children.Add(Ui.Text("Resets " + reset.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture), 11, "#A0A0A6"));
            }
            if (reading.State != ReadingState.Ready) content.Children.Add(Ui.Text(reading.Message ?? reading.State.ToString(), 12, "#F2C66D"));
        }
        if (store.Usage.TryGetValue(id, out var local)) content.Children.Add(Ui.Text($"Today · This PC     {TokenFormatter.Format(local.Today.TotalTokens, settings.Current.NumberStyle)} tokens", 12));
        foreach (var session in store.Sessions.Where(x => x.Provider == id).Take(8)) content.Children.Add(Ui.Text($"{session.Name} · {session.State} · {DateTimeOffset.Now - session.Since:h\\:mm\\:ss}", 12));
        return new Border { Child = content, Background = Brushes.Black, CornerRadius = new CornerRadius(16) };
    }
    private Screen SelectedScreen() => Screen.AllScreens.FirstOrDefault(x => x.DeviceName == settings.Current.Display) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
    private void Position()
    {
        var screen = SelectedScreen(); var area = screen.WorkingArea; var dpi = ScreenScale(screen);
        var position = NotchGeometry.Place(new ScreenArea(area.X, area.Y, area.Width, area.Height), Width * dpi, Height * dpi,
            settings.Current.Edge, settings.Current.Offset * dpi);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetWindowPos(handle, new IntPtr(-1), (int)position.X, (int)position.Y, (int)Math.Ceiling(Width * dpi), (int)Math.Ceiling(Height * dpi), 0x10);
    }
    private static double ScreenScale(Screen screen)
    {
        var point = new NativePoint { X = screen.Bounds.X + screen.Bounds.Width / 2, Y = screen.Bounds.Y + screen.Bounds.Height / 2 };
        var monitor = MonitorFromPoint(point, 2);
        return GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? Math.Max(96, x) / 96d : 1;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
#pragma warning disable SYSLIB1054
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
#pragma warning restore SYSLIB1054
}
