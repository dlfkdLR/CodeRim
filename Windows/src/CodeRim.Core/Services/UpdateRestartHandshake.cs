using System.Diagnostics;
using System.Text;

namespace CodeRim.Core.Services;

internal static class UpdateRestartHandshake
{
    internal static async Task ConfirmAsync(string launcher, string operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var confirmation = Path.Combine(launcher, "restart.confirmed");
        try
        {
            token.ThrowIfCancellationRequested();
            InstallFileSystem.WriteNew(confirmation, Encoding.ASCII.GetBytes(operation));
            var accepted = Path.Combine(launcher, "restart.accepted");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (!File.Exists(accepted)) await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            if (Encoding.ASCII.GetString(InstallFileSystem.ReadBounded(accepted, 32)) != operation) throw new InvalidDataException("The restart handshake does not match.");
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            // The supervisor continues to watch both signals while waiting for this exact parent to exit.
            // Removing the existing confirmation also works when the disk cannot allocate a new cancellation marker.
            InstallFileSystem.CheckPath(confirmation); if (File.Exists(confirmation)) File.Delete(confirmation);
            var cancelled = Path.Combine(launcher, "restart.cancelled");
            try { if (!File.Exists(cancelled)) InstallFileSystem.WriteNew(cancelled, Encoding.ASCII.GetBytes(operation)); }
            catch (Exception write) when (UpdateBootstrap.Expected(write)) { }
            throw;
        }
    }
    internal static async Task<bool> WaitAsync(string launcher, string operation, UpdateParent parent, TimeSpan budget, CancellationToken token)
    {
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(budget));
        token.ThrowIfCancellationRequested();
        var confirmation = Path.Combine(launcher, "restart.confirmed"); var cancelled = Path.Combine(launcher, "restart.cancelled"); var until = Stopwatch.StartNew();
        while (!File.Exists(confirmation) && !File.Exists(cancelled) && until.Elapsed < budget) await Task.Delay(25, token).ConfigureAwait(false);
        if (!File.Exists(confirmation) || File.Exists(cancelled) || Encoding.ASCII.GetString(InstallFileSystem.ReadBounded(confirmation, 32)) != operation) return false;
        token.ThrowIfCancellationRequested();
        InstallFileSystem.WriteNew(Path.Combine(launcher, "restart.accepted"), Encoding.ASCII.GetBytes(operation));
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var exited = parent.WaitAsync(budget, waitCancellation.Token);
        try
        {
            while (!exited.IsCompleted)
            {
                if (!File.Exists(confirmation) || File.Exists(cancelled)) { waitCancellation.Cancel(); try { _ = await exited.ConfigureAwait(false); } catch (OperationCanceledException) { } return false; }
                await Task.WhenAny(exited, Task.Delay(25, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            var didExit = await exited.ConfigureAwait(false); token.ThrowIfCancellationRequested();
            return didExit && File.Exists(confirmation) && !File.Exists(cancelled);
        }
        finally { waitCancellation.Cancel(); }
    }
}
