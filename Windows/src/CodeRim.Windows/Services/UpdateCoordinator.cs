using System.IO;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal static class UpdateCoordinator
{
    internal static bool SigningConfigured => UpdateBootstrap.IsSigningConfigured(typeof(App).Assembly);
    internal static async Task<UpdateExecutionResult> PrepareAsync(ReleasePackage package, CancellationToken token)
    {
        DownloadedReleasePackage? download = null;
        try
        {
            download = await ReleasePackageDownload.DownloadAsync(package, UpdateBootstrap.DownloadDirectory(), token).ConfigureAwait(false);
            return await UpdateBootstrap.PrepareAsync(download, typeof(App).Assembly, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(UpdateExecutionStatus.CancelledBeforeStart); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or InvalidOperationException)
        { return new(UpdateExecutionStatus.PackageRejected); }
        finally
        {
            // Only this invocation's download; the worker owns a separately rehashed capsule copy after Prepared.
            if (download is not null)
                try { File.Delete(download.Path); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}
