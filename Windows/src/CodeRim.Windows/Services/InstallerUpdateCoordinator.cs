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

    internal static async Task<string?> CheckAndDownloadAsync(bool automatic, CancellationToken token = default)
    {
        if (!IsManaged) return null;
        if (automatic) { if (!await Gate.WaitAsync(0, token).ConfigureAwait(false)) return null; }
        else await Gate.WaitAsync(token).ConfigureAwait(false);
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
                ?? InstallerUpdates.Cache(await ReleasePackageDownload.DownloadAsync(package, directory, token).ConfigureAwait(false));
            InstallerUpdates.CleanCache(directory, downloaded.Path);
            var previous = ready; ready = downloaded; lastCheck = DateTimeOffset.UtcNow;
            if (previous is not null && previous.Path != downloaded.Path) DeleteDownload(previous.Path);
            return ReadyVersion;
        }
        finally { Gate.Release(); }
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
