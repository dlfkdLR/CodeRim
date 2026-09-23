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
    private InstanceActivation? activation;
    private TrayIconHost? tray;
    private DashboardStore? store;
    private AppSettingsStore? settings;
    private CredentialVault? vault;
    private NotchWindow? notch;
    private DashboardWindow? dashboard;
    private SessionWatcher? watcher;
    private readonly DispatcherTimer timer = new();
    private readonly DispatcherTimer activityTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly ThresholdTracker thresholds = new();
    private bool smokeTest;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if CODERIM_MSI_QA
        if (e.Args.Length == 2 && e.Args[0] == "--qa-msi-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var pin = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                    .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "CodeRimInstallerWorkerSha256").Value ?? "";
                var operation = await MsiUpdateQa.StartAsync(e.Args[1], pin).ConfigureAwait(true);
                MsiUpdateExecution.Confirm(operation); Shutdown();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { File.WriteAllText(Path.Combine(e.Args[1], "handoff-error.txt"), error.ToString()); Shutdown(1); }
            return;
        }
#endif
        if (e.Args.Contains("--smoke-test", StringComparer.Ordinal) && e.Args.Contains("--antigravity-fixture-server", StringComparer.Ordinal))
        {
            try { await NativeSmoke.RunAntigravityFixtureAsync(); Shutdown(); }
            catch (Exception error) when (error is not OutOfMemoryException) { Shutdown(1); }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == UpdateBootstrap.EntryArgument)
        {
            // No settings, keychain, vault, companion publisher, single-instance signalling or normal application UI.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await UpdateBootstrap.RunEntryAsync(e.Args, typeof(App).Assembly).ConfigureAwait(true);
            Shutdown(); return;
        }
        SettingsTheme.Apply();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += AppearanceChanged;
        smokeTest = e.Args.Contains("--smoke-test", StringComparer.Ordinal);
        if (smokeTest)
        {
            var temp = Path.Combine(Path.GetTempPath(), "CodeRim-Smoke-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("CODERIM_DATA_DIR", temp);
        }
        var instanceName = smokeTest ? "CodeRim.Smoke." + Environment.ProcessId : InstanceActivation.UserName;
        instance = new Mutex(true, "Local\\" + instanceName, out var created);
        if (!created) { await InstanceActivation.NotifyAsync(instanceName); Shutdown(); return; }
        settings = new AppSettingsStore(); CredentialVault.RestrictDirectory(CompanionFile.DataDirectory); vault = new CredentialVault();
        if (!smokeTest && InstallerUpdateCoordinator.IsManaged && settings.Current.LaunchAtLogin)
            try { StartupService.SetEnabled(true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException) { /* Login registration must not prevent the app from opening. */ }
        Motion.SetReduced(settings.Current.ReduceMotion);
        store = new DashboardStore(settings, vault, smokeTest);
        notch = new NotchWindow(store, settings, ShowSettings);
        tray = new TrayIconHost(() => ShowSettings("usage"), () => _ = store.RefreshAsync(true), () => ShowSettings(null), ShutdownApplication);
        if (!smokeTest) activation = new InstanceActivation(instanceName, () => Dispatcher.BeginInvoke(() => ShowSettings("usage")));
        tray.ShowNotchRequested += () => { settings.RevealNotch(); notch.Peek(); };
        store.SessionAttentionRequested += session => { if (settings.Current.PeekOnCompletion) notch.Peek(session); };
        store.ReadingUpdated += reading => { if (settings.Current.AlertsEnabled && !settings.Current.MutedAlertProviders.Contains(reading.Id, StringComparer.Ordinal)) foreach (var threshold in thresholds.Observe(reading, DateTimeOffset.Now)) tray.Notify(ProviderCatalog.Find(reading.Id)?.Name ?? reading.Id, threshold == 100 ? "Usage limit reached." : "Usage has reached 80%."); };
        settings.SettingsChanged += (_, _) => { Motion.SetReduced(settings.Current.ReduceMotion); ConfigureTimer(); };
        timer.Tick += (_, _) => { watcher?.Rebuild(); _ = store.RefreshAsync(); };
        activityTimer.Tick += (_, _) => _ = store.RefreshActivityAsync();
        if (!smokeTest) activityTimer.Start();
        ConfigureTimer(); notch.ApplyVisibility();
        if (!smokeTest) { updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(); updateTimer.Start(); _ = CheckUpdatesAsync(); }
        _ = StartAsync(e.Args);
    }
    private void AppearanceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => { SettingsTheme.Apply(); Motion.RefreshPolicy(); });
    private async Task StartAsync(string[] args)
    {
        if (store is null) return;
        await store.RefreshAsync().ConfigureAwait(true);
        if (smokeTest)
        {
            var outputIndex = Array.IndexOf(args, "--capture");
            var output = outputIndex >= 0 && outputIndex + 1 < args.Length ? args[outputIndex + 1] : Path.Combine(Path.GetTempPath(), "windows-dashboard.png");
            try
            {
                ShowSettings("usage");
                if (dashboard is null || notch is null || settings is null) throw new InvalidOperationException("UI was not created.");
                await NativeSmoke.RunAsync(dashboard, notch, store, settings, output).ConfigureAwait(true);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (dashboard is not null) NativeSmoke.Capture(dashboard, Path.ChangeExtension(output, ".failure.png"));
                File.WriteAllText(Path.ChangeExtension(output, ".error.txt"), error.ToString());
                Shutdown(1); return;
            }
            ShutdownApplication();
        }
    }
    private async Task CheckUpdatesAsync()
    {
        if (settings?.Current.CheckForUpdates != true) return;
        if (InstallerUpdateCoordinator.IsManaged)
        {
            try
            {
                if (await InstallerUpdateCoordinator.CheckAndDownloadAsync(automatic: true).ConfigureAwait(true) is { } ready && settings.Current.CheckForUpdates)
                    tray?.Notify("CodeRim update", "Version " + ready + " is ready. Open Information to restart and install.");
            }
            catch (Exception error) when (error is not OutOfMemoryException) { /* Retry on the next timer tick; manual checks show failures. */ }
            return;
        }
        if (await UpdateNotifications.CheckAsync().ConfigureAwait(true) is { } version && settings.Current.CheckForUpdates) tray?.Notify("CodeRim update", "Version " + version + " is available. Open Information to download it.");
    }
    private void ConfigureTimer()
    {
        timer.Stop();
        if (smokeTest || settings is null || settings.Current.RefreshIntervalSeconds <= 0)
        {
            watcher?.Dispose(); watcher = null; return;
        }
        if (!smokeTest && watcher is null)
            watcher = new SessionWatcher(paths => Dispatcher.BeginInvoke(() =>
            {
                if (settings.Current.RefreshIntervalSeconds <= 0 || store is null) return;
                store.Invalidate(paths); _ = store.RefreshAsync();
            }));
        timer.Interval = TimeSpan.FromSeconds(settings.Current.RefreshIntervalSeconds); timer.Start();
    }
    private void ShowSettings(string? page)
    {
        if (settings is null || store is null || vault is null) return;
        if (dashboard is null) { dashboard = new DashboardWindow(store, settings, vault); dashboard.Closed += (_, _) => dashboard = null; }
        dashboard.Navigate(page ?? "general");
    }
    internal void ShutdownApplication() { dashboard?.Close(); notch?.Close(); Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= AppearanceChanged;
        timer.Stop(); activityTimer.Stop(); updateTimer.Stop(); watcher?.Dispose(); store?.Dispose(); tray?.Dispose(); activation?.Dispose(); instance?.Dispose();
        base.OnExit(e);
    }
}
#pragma warning restore CA1001
