using System.Runtime.Versioning;

namespace CodeRim.Core.Services;

[SupportedOSPlatform("windows")]
internal static class UpdateBootstrapVerification
{
    private static readonly SemaphoreSlim Pending = new(1, 1);
    internal static async Task<WindowsPublisherTrust> VerifyAsync(string path, PublisherPin pin, CancellationToken token)
    {
        // Executing the path first cannot establish the identity of the bytes at that path. Verify and retain the image lease here.
        // The UI can cancel/stop observing; at most one native verification may remain pending in this process.
        if (!await Pending.WaitAsync(0, token).ConfigureAwait(false)) throw new IOException("A previous publisher verification is still finishing.");
        var verification = Task.Run(() => { try { return WindowsPublisherTrust.Verify(path, pin); } finally { Pending.Release(); } }, CancellationToken.None);
        try
        {
            var image = await verification.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); return image;
        }
        catch
        {
            _ = verification.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); else _ = task.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
}
