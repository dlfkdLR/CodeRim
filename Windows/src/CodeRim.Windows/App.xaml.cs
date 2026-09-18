using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.Tray;
using CodeRim.Windows.ViewModels;
using CodeRim.Windows.Views;

namespace CodeRim.Windows;

#pragma warning disable CA1001 // WPF owns the application lifetime; OnExit disposes owned resources.
public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private TrayIconHost? tray;
    private DashboardStore? store;
    private AppSettingsStore? settings;
    private CredentialVault? vault;
    private NotchWindow? notch;
    private DashboardWindow? dashboard;
    private SessionWatcher? watcher;
    private readonly DispatcherTimer timer = new();
    private readonly ThresholdTracker thresholds = new();
    private bool smokeTest;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        smokeTest = e.Args.Contains("--smoke-test", StringComparer.Ordinal);
        if (smokeTest)
        {
            var temp = Path.Combine(Path.GetTempPath(), "CodeRim-Smoke-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("CODERIM_DATA_DIR", temp);
        }
        instance = new Mutex(true, smokeTest ? "Local\\CodeRim.Smoke." + Environment.ProcessId : "Local\\CodeRim.Windows", out var created);
        if (!created) { Shutdown(); return; }
        settings = new AppSettingsStore(); CredentialVault.RestrictDirectory(CompanionFile.DataDirectory); vault = new CredentialVault();
        store = new DashboardStore(settings, vault, smokeTest);
        notch = new NotchWindow(store, settings, ShowSettings);
        tray = new TrayIconHost(() => ShowSettings("usage"), () => _ = store.RefreshAsync(true), () => ShowSettings(null), ShutdownApplication);
        tray.ShowNotchRequested += () => settings.Save(settings.Current with { Visibility = NotchVisibility.OnHover });
        store.ReadingUpdated += reading => { if (settings.Current.AlertsEnabled) foreach (var threshold in thresholds.Observe(reading, DateTimeOffset.Now)) tray.Notify(ProviderCatalog.Find(reading.Id)?.Name ?? reading.Id, threshold == 100 ? "Usage limit reached." : "Usage has reached 80%."); };
        if (!smokeTest) watcher = new SessionWatcher(paths => Dispatcher.BeginInvoke(() => { store.Invalidate(paths); _ = store.RefreshAsync(); }));
        settings.SettingsChanged += (_, _) => ConfigureTimer();
        timer.Tick += (_, _) => { watcher?.Rebuild(); _ = store.RefreshAsync(); };
        ConfigureTimer(); notch.ApplyVisibility();
        _ = StartAsync(e.Args);
    }
    private async Task StartAsync(string[] args)
    {
        if (store is null) return;
        await store.RefreshAsync().ConfigureAwait(true);
        if (smokeTest)
        {
            ShowSettings("usage");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (dashboard is null) throw new InvalidOperationException("The dashboard was not created.");
            dashboard.UpdateLayout();
            var outputIndex = Array.IndexOf(args, "--capture");
            if (outputIndex >= 0 && outputIndex + 1 < args.Length)
            {
                var image = new RenderTargetBitmap((int)dashboard.ActualWidth, (int)dashboard.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                image.Render(dashboard); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(args[outputIndex + 1]); encoder.Save(file);
            }
            ShutdownApplication();
        }
    }
    private void ConfigureTimer()
    {
        timer.Stop();
        if (settings is not null && settings.Current.RefreshIntervalSeconds > 0)
        { timer.Interval = TimeSpan.FromSeconds(settings.Current.RefreshIntervalSeconds); timer.Start(); }
    }
    private void ShowSettings(string? page)
    {
        if (settings is null || store is null || vault is null) return;
        if (dashboard is null) { dashboard = new DashboardWindow(store, settings, vault); dashboard.Closed += (_, _) => dashboard = null; }
        dashboard.Navigate(page ?? "general");
    }
    private void ShutdownApplication() { dashboard?.Close(); notch?.Close(); Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        timer.Stop(); watcher?.Dispose(); store?.Dispose(); tray?.Dispose(); instance?.Dispose();
        base.OnExit(e);
    }
}
#pragma warning restore CA1001
