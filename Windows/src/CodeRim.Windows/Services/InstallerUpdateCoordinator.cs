using System.Diagnostics;
using System.IO;
using System.Threading;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal static class InstallerUpdateCoordinator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset lastCheck;
    private static DownloadedReleasePackage? ready;
    private static InstallerAuthorization? authorization;
    internal static bool IsManaged => File.Exists(Path.Combine(AppContext.BaseDirectory, "CodeRim.install.json"));
    internal static string? ReadyVersion => ready?.Package.Version.ToString(3);
    // Whoever is waiting to see progress — the Information page — even when a background check started the download.
    private static IProgress<double>? watcher;
    private static readonly IProgress<double> Relay = new RelayProgress();
    private sealed class RelayProgress : IProgress<double> { public void Report(double value) => Volatile.Read(ref watcher)?.Report(value); }

    internal static async Task<string?> CheckAndDownloadAsync(bool automatic, IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (!IsManaged) return null;
        if (automatic) { if (!await Gate.WaitAsync(0, token).ConfigureAwait(false)) return null; }
        else
        {
            // A manual check while the background download runs follows that download instead of sitting silent.
            if (progress is not null) Volatile.Write(ref watcher, progress);
            try { await Gate.WaitAsync(token).ConfigureAwait(false); }
            catch { if (progress is not null) Interlocked.CompareExchange(ref watcher, null, progress); throw; }
        }
        try
        {
            var age = DateTimeOffset.UtcNow - lastCheck;
            if (automatic && age >= TimeSpan.Zero && age < TimeSpan.FromDays(1)) return null;
            var update = await ReleaseUpdates.CheckAsync(UpdateNotifications.Architecture, token).ConfigureAwait(false);
            if (!update.IsNewer) { lastCheck = DateTimeOffset.UtcNow; InstallerUpdates.CleanCache(UpdateBootstrap.DownloadDirectory(), null); ready = null; return null; }
            var package = update.Package;
            if (package is null || !package.IsInstaller) throw new InvalidDataException("A complete installer release is not available.");
            authorization = await InstallerUpdates.AuthenticateAsync(package, token).ConfigureAwait(false);
            if (ready?.Package == package) { lastCheck = DateTimeOffset.UtcNow; return ReadyVersion; }
            var directory = UpdateBootstrap.DownloadDirectory();
            var downloaded = InstallerUpdates.FindCached(package, directory)
                ?? InstallerUpdates.Cache(await ReleasePackageDownload.DownloadAsync(package, directory, Relay, token).ConfigureAwait(false));
            InstallerUpdates.CleanCache(directory, downloaded.Path);
            var previous = ready; ready = downloaded; lastCheck = DateTimeOffset.UtcNow;
            if (previous is not null && previous.Path != downloaded.Path) DeleteDownload(previous.Path);
            return ReadyVersion;
        }
        finally { if (progress is not null) Interlocked.CompareExchange(ref watcher, null, progress); Gate.Release(); }
    }

    internal static async Task<string> PrepareRestartAsync(CancellationToken token)
    {
        if (!await Gate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("An update download is still running.");
        try
        {
            if (authorization is null || ready?.Package != authorization.Package || !IsManaged)
                throw new InvalidOperationException("No verified installer is ready.");
            var pin = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "CodeRimInstallerWorkerSha256").Value ?? "";
            return await MsiUpdateExecution.StartAsync(authorization, pin, token).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static void DeleteDownload(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
