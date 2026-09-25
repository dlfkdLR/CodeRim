namespace CodeRim.Core.Services;

public sealed record ClaudeIntegrationPreferences(bool Enabled, string? LinkedAccountId);

/// <summary>No credentials cross this boundary. Implementations verify the CLI's active login.</summary>
public interface IClaudeIntegrationOperations
{
    string? CurrentAccountId { get; }
    Task<LoginIdentity?> ProbeAsync(CancellationToken token);
    Task InstallAsync(bool replaceExisting, CancellationToken token);
    Task UninstallAsync(CancellationToken token);
}

/// <summary>
/// Owned by one UI context. Account approval is independent of notch visibility and
/// helper health. The gate serializes filesystem side effects, not just UI results.
/// </summary>
public sealed class ClaudeIntegration : IDisposable
{
    private readonly IClaudeIntegrationOperations operations;
    private readonly Action<ClaudeIntegrationPreferences, bool> save;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Task? refreshing;
    private int mutations;
    private bool disposed;
    public ClaudeIntegrationPreferences Preferences { get; private set; }
    public LoginIdentity? DetectedAccount { get; private set; }
    public LoginIdentity? Account { get; private set; }
    public bool Switching { get; private set; }
    public bool ChangingConnection => mutations > 0 || Switching;
    public bool Busy => ChangingConnection || refreshing is not null;
    public bool Available => !disposed && mutations == 0 && !Switching && Preferences.Enabled
        && Account is { } account && account.Id == Preferences.LinkedAccountId
        && account.Id == operations.CurrentAccountId;
    public string Message { get; private set; }
    public string? HelperError { get; private set; }
    public event Action? Changed;

    public ClaudeIntegration(ClaudeIntegrationPreferences preferences, IClaudeIntegrationOperations operations,
        Action<ClaudeIntegrationPreferences, bool> save, string? initialError = null)
    {
        Preferences = preferences; this.operations = operations; this.save = save;
        Message = initialError ?? (preferences.Enabled ? "Checking Claude account…" : "Claude is turned off");
    }

    public Task RefreshAsync()
    {
        if (disposed || mutations > 0 || Switching || !Preferences.Enabled) return Task.CompletedTask;
        if (refreshing is { } active) return active;
        // Register before scheduling: Yield may resume on another thread before
        // the right-hand side of an assignment has returned to its caller.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        refreshing = completion.Task;
        _ = CompleteRefreshAsync(completion);
        return completion.Task;
    }
    private async Task CompleteRefreshAsync(TaskCompletionSource completion)
    {
        try { await RefreshCoreAsync().ConfigureAwait(true); completion.SetResult(); }
        catch (OperationCanceledException error) { completion.SetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.SetException(error); }
    }
    private async Task RefreshCoreAsync()
    {
        await Task.Yield();
        try
        {
            Notify();
            await gate.WaitAsync(lifetime.Token).ConfigureAwait(true);
            try
            {
                if (mutations == 0 && !Switching && Preferences.Enabled)
                    await ProbeAndConnectAsync(link: false).ConfigureAwait(true);
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { refreshing = null; Notify(); }
    }
    public async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (disposed || Switching || mutations > 0) return false;
        if (Preferences.Enabled == enabled) return true;
        return await MutateAsync(async () =>
        {
            if (enabled)
            {
                Commit(Preferences with { Enabled = true });
                await ProbeAndConnectAsync(link: false).ConfigureAwait(true);
            }
            else
            {
                await RemoveHelperAndCommitAsync(Preferences with { Enabled = false }).ConfigureAwait(true);
                ClearAccount(); DetectedAccount = null; Message = "Claude is turned off";
            }
        }, enabled ? "Claude could not be turned on. Try again." : "Claude could not be turned off safely. Try again.").ConfigureAwait(true);
    }
    public async Task<bool> AddCurrentAccountAsync(bool replaceExisting = false)
    {
        if (disposed || !Preferences.Enabled || Busy) return false;
        return await MutateAsync(() => ProbeAndConnectAsync(link: true, replaceExisting), "Claude account could not be added. Try again.").ConfigureAwait(true) && Available;
    }
    public Task<bool> DisconnectAsync() => disposed || Switching || mutations > 0 ? Task.FromResult(false)
        : MutateAsync(async () =>
        {
            await RemoveHelperAndCommitAsync(Preferences with { LinkedAccountId = null }).ConfigureAwait(true);
            ClearAccount(); Message = "Add a Claude account to continue";
        }, "Claude could not be disconnected safely. Try again.");

    private async Task RemoveHelperAndCommitAsync(ClaudeIntegrationPreferences next)
    {
        await operations.UninstallAsync(lifetime.Token).ConfigureAwait(true);
        try { Commit(next, selectCodex: true); }
        catch
        {
            // The settings write is the commit boundary. Restore only our own
            // helper through its exact-compare installer; never overwrite edits.
            if (Preferences.Enabled && Account is { } account && operations.CurrentAccountId == account.Id)
            {
                try { await operations.InstallAsync(false, lifetime.Token).ConfigureAwait(true); }
                catch (Exception error) when (error is not OutOfMemoryException) { HelperError = "Claude limits could not be restored. Refresh to retry."; }
            }
            throw;
        }
    }
    private async Task<bool> MutateAsync(Func<Task> action, string failure)
    {
        mutations++;
        var entered = false;
        try
        {
            Notify();
            await gate.WaitAsync(lifetime.Token).ConfigureAwait(true); entered = true;
            if (disposed) return false;
            await action().ConfigureAwait(true); return true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Message = failure; return false; }
        finally { if (entered) gate.Release(); mutations--; Notify(); }
    }
    private async Task ProbeAndConnectAsync(bool link, bool replaceExisting = false, string? expectedAccountId = null)
    {
        Message = "Checking Claude account…"; Notify();
        LoginIdentity? found;
        try { found = await operations.ProbeAsync(lifetime.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A transient probe failure may retain only the exact current owner.
            if (Account is not { } previous || operations.CurrentAccountId != previous.Id) { ClearAccount(); DetectedAccount = null; }
            Message = "Claude account check failed. Try again."; return;
        }
        if (disposed || Switching) return;
        DetectedAccount = found;
        if (found is null || operations.CurrentAccountId != found.Id)
        { ClearAccount(); Message = "Sign in to Claude Code, then add the account"; return; }
        if (expectedAccountId is not null && found.Id != expectedAccountId)
        { ClearAccount(); Message = "The Claude account changed. Add the current account."; return; }
        if (link) Commit(Preferences with { LinkedAccountId = found.Id });
        if (Preferences.LinkedAccountId != found.Id)
        { ClearAccount(); Message = "Add this Claude account to CodeRim"; return; }
        Account = found; HelperError = null;
        try { await operations.InstallAsync(replaceExisting, lifetime.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { HelperError = "Claude limits could not be set up. Refresh to retry."; }
        if (disposed || Switching) return;
        if (operations.CurrentAccountId != found.Id)
        { ClearAccount(); DetectedAccount = null; Message = "The Claude account changed. Add the current account."; return; }
        Message = HelperError ?? "Use Claude Code once, then refresh";
    }
    public async Task BeginAccountSwitchAsync()
    {
        if (disposed || Switching || mutations > 0) throw new InvalidOperationException("Wait for the Claude connection operation to finish.");
        Switching = true; ClearAccount(); DetectedAccount = null; Message = "Switching Claude account…"; Notify();
        // A late helper install must finish before the caller changes auth files.
        await gate.WaitAsync(lifetime.Token).ConfigureAwait(true); gate.Release();
    }
    public async Task FinishAccountSwitchAsync(string? verifiedAccountId)
    {
        if (!Switching || disposed) return;
        Switching = false;
        if (Preferences.Enabled)
        {
            // A failed switch does not approve an externally changed account.
            await MutateAsync(() => ProbeAndConnectAsync(link: verifiedAccountId is not null, expectedAccountId: verifiedAccountId), "Claude account could not be verified. Try again.").ConfigureAwait(true);
        }
        else { Message = "Claude is turned off"; Notify(); }
    }
    private void Commit(ClaudeIntegrationPreferences next, bool selectCodex = false)
    {
        lifetime.Token.ThrowIfCancellationRequested(); save(next, selectCodex); Preferences = next;
    }
    private void ClearAccount() { Account = null; HelperError = null; }
    private void Notify() { if (!disposed) Changed?.Invoke(); }
    public void Dispose() { disposed = true; lifetime.Cancel(); }
}
