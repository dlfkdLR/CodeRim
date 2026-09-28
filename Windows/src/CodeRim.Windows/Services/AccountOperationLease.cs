using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace CodeRim.Windows.Services;

internal sealed class AccountOperationBusyException(bool otherSession = false) : InvalidOperationException(
    otherSession ? AccountOperationLease.BusyMessage : LocalMessage)
{
    internal const string LocalMessage = "An account operation is already in progress.";
    internal string UserMessage { get; } = otherSession ? AccountOperationLease.BusyMessage : LocalMessage;
}

// The existing account mutation entrypoints are WPF-dispatcher operations.
// Mutex acquisition and release must stay on that thread across their awaits.
internal sealed class AccountOperationLease : IDisposable
{
    internal const string BusyMessage = "Another CodeRim session is performing an account operation. Wait for it to finish.";
    internal const string UiThreadMessage = "Account operations must run on the application dispatcher.";
    private Mutex? mutex;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    internal bool WasAbandoned { get; }

    private AccountOperationLease(Mutex mutex, bool abandoned)
    {
        this.mutex = mutex; WasAbandoned = abandoned;
    }

    internal static string Name
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new IOException("The current Windows account is unavailable.");
            return "CodeRim.AccountOperations." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));
        }
    }

    internal static AccountOperationLease Acquire() => Acquire(Name);
    internal static AccountOperationLease Acquire(string name)
    {
        if (Application.Current?.Dispatcher.CheckAccess() != true
            || SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException(UiThreadMessage);
        // One name for all providers, data directories and Windows sessions.
        // Validate the current-user boundary even when an object already exists.
        var options = new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false };
        var handle = new Mutex(false, name, options);
        var owned = false; var abandoned = false;
        try
        {
            try { owned = handle.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; abandoned = true; }
            if (!owned) throw new AccountOperationBusyException(otherSession: true);
            return new AccountOperationLease(handle, abandoned);
        }
        catch
        {
            try { if (owned) handle.ReleaseMutex(); }
            finally { handle.Dispose(); }
            throw;
        }
    }

    public void Dispose()
    {
        var handle = mutex;
        if (handle is null) return;
        if (ownerThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException(UiThreadMessage);
        try { handle.ReleaseMutex(); }
        finally { mutex = null; handle.Dispose(); }
    }
}
