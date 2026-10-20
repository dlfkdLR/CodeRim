using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace CodeRim.Windows.Services;

/// <summary>A current-user-only pipe accepts one fixed activation byte, never paths or commands.</summary>
internal sealed class InstanceActivation : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task listener;
    internal static string UserName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new InvalidOperationException("No Windows user identity.");
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return "CodeRim." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid))) + ".Session." + process.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
    internal InstanceActivation(string name, Action activate) => listener = ListenAsync(name, activate, lifetime.Token);
    private static async Task ListenAsync(string name, Action activate, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var message = new byte[1];
                if (await pipe.ReadAsync(message, deadline.Token).ConfigureAwait(false) == 1 && message[0] == 1 && !token.IsCancellationRequested)
                    activate();
            }
            catch (OperationCanceledException) { if (token.IsCancellationRequested) return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                try { await Task.Delay(100, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
    internal static async Task<bool> NotifyAsync(string name, CancellationToken token = default)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException) { return false; }
    }
    public void Dispose()
    {
        lifetime.Cancel();
        _ = listener.ContinueWith(_ => lifetime.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
