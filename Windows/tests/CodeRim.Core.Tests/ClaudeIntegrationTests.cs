using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class ClaudeIntegrationTests
{
    private static readonly LoginIdentity A = new("owner-a", "a@example.invalid", "org-a", "max");
    private static readonly LoginIdentity B = new("owner-b", "b@example.invalid", "org-b", "pro");
    private sealed class Operations : IClaudeIntegrationOperations
    {
        public LoginIdentity? Current = A;
        public string? CurrentAccountId => Current?.Id;
        public int Probes, Installs, Uninstalls;
        public bool Installed, FailInstall, FailUninstall, FailProbe;
        public TaskCompletionSource? ProbeGate, InstallGate;
        public async Task<LoginIdentity?> ProbeAsync(CancellationToken token)
        {
            Probes++; if (ProbeGate is { } gate) await gate.Task.WaitAsync(token);
            if (FailProbe) throw new IOException("private details must not enter presentation");
            return Current;
        }
        public async Task InstallAsync(bool replaceExisting, CancellationToken token)
        {
            Installs++; if (InstallGate is { } gate) await gate.Task; // Deliberately uncancellable side effect.
            if (FailInstall) throw new IOException("private path"); Installed = true;
        }
        public Task UninstallAsync(CancellationToken token)
        {
            Uninstalls++; if (FailUninstall) throw new IOException("private path"); Installed = false; return Task.CompletedTask;
        }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly Operations Ops = new();
        public readonly ClaudeIntegration Store;
        public ClaudeIntegrationPreferences Saved;
        public bool FailSave, SelectedCodex;
        public Fixture(bool enabled = true, string? linked = null)
        {
            Saved = new(enabled, linked);
            Store = new(Saved, Ops, (next, select) => { if (FailSave) throw new IOException("secret"); Saved = next; SelectedCodex |= select; });
        }
        public void Dispose() => Store.Dispose();
    }
    [Fact]
    public async Task EnableOnlyDetectsAndExplicitAddApproves()
    {
        using var f = new Fixture(false);
        await f.Store.RefreshAsync(); Assert.Equal(0, f.Ops.Probes);
        Assert.True(await f.Store.SetEnabledAsync(true));
        Assert.Equal(A, f.Store.DetectedAccount); Assert.Null(f.Saved.LinkedAccountId);
        Assert.False(f.Store.Available); Assert.Equal(0, f.Ops.Installs);
        Assert.True(await f.Store.AddCurrentAccountAsync());
        Assert.True(f.Store.Available); Assert.Equal(A.Id, f.Saved.LinkedAccountId); Assert.True(f.Ops.Installed);
    }
    [Fact]
    public async Task HelperFailureDoesNotDisableLocalUsage()
    {
        using var f = new Fixture(true, A.Id); f.Ops.FailInstall = true;
        await f.Store.RefreshAsync(); Assert.True(f.Store.Available);
        Assert.NotNull(f.Store.HelperError); Assert.DoesNotContain("private", f.Store.Message);
    }
    [Fact]
    public async Task DisableRetainsLinkButDisconnectRemovesIt()
    {
        using var f = new Fixture(true, A.Id); await f.Store.RefreshAsync();
        Assert.True(await f.Store.SetEnabledAsync(false)); Assert.False(f.Store.Available);
        Assert.False(f.Saved.Enabled); Assert.Equal(A.Id, f.Saved.LinkedAccountId); Assert.True(f.SelectedCodex); Assert.False(f.Ops.Installed);
        Assert.True(await f.Store.SetEnabledAsync(true)); Assert.True(f.Store.Available);
        Assert.True(await f.Store.DisconnectAsync()); Assert.True(f.Saved.Enabled); Assert.Null(f.Saved.LinkedAccountId);
        Assert.False(f.Store.Available); await f.Store.RefreshAsync(); Assert.False(f.Store.Available);
    }
    [Fact]
    public async Task ExternalOwnerChangeRequiresNewApprovalAndImmediatelyHidesAvailability()
    {
        using var f = new Fixture(true, A.Id); await f.Store.RefreshAsync();
        f.Ops.Current = B; Assert.False(f.Store.Available);
        await f.Store.RefreshAsync(); Assert.Null(f.Store.Account); Assert.Equal(B, f.Store.DetectedAccount);
        Assert.Equal(A.Id, f.Saved.LinkedAccountId); Assert.Equal(1, f.Ops.Installs);
        await f.Store.AddCurrentAccountAsync(); Assert.True(f.Store.Available); Assert.Equal(B.Id, f.Saved.LinkedAccountId);
    }
    [Fact]
    public async Task FailedProbeRetainsOnlyVerifiedSameOwner()
    {
        using var f = new Fixture(true, A.Id); await f.Store.RefreshAsync(); f.Ops.FailProbe = true;
        await f.Store.RefreshAsync(); Assert.True(f.Store.Available);
        f.Ops.Current = B; await f.Store.RefreshAsync(); Assert.False(f.Store.Available); Assert.Null(f.Store.Account);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UninstallFailureDoesNotCommitDisabledOrUnlinked(bool disconnect)
    {
        using var f = new Fixture(true, A.Id); await f.Store.RefreshAsync(); f.Ops.FailUninstall = true;
        Assert.False(await (disconnect ? f.Store.DisconnectAsync() : f.Store.SetEnabledAsync(false)));
        Assert.True(f.Saved.Enabled); Assert.Equal(A.Id, f.Saved.LinkedAccountId); Assert.True(f.Store.Available);
        Assert.False(f.SelectedCodex); Assert.DoesNotContain("private", f.Store.Message);
    }
    [Fact]
    public async Task SaveFailureAfterUninstallRestoresOnlyHelperWithoutClaimingSuccess()
    {
        using var f = new Fixture(true, A.Id); await f.Store.RefreshAsync(); f.FailSave = true;
        Assert.False(await f.Store.SetEnabledAsync(false));
        Assert.True(f.Saved.Enabled); Assert.True(f.Store.Available); Assert.True(f.Ops.Installed); Assert.Equal(2, f.Ops.Installs);
    }
    [Fact]
    public async Task FailedEnableSaveDoesNotProbeOrChangeState()
    {
        using var f = new Fixture(false); f.FailSave = true;
        Assert.False(await f.Store.SetEnabledAsync(true)); Assert.False(f.Store.Preferences.Enabled); Assert.Equal(0, f.Ops.Probes);
    }
    [Fact]
    public async Task FailedLinkSaveDoesNotInstallOrConnect()
    {
        using var f = new Fixture(); f.FailSave = true;
        Assert.False(await f.Store.AddCurrentAccountAsync()); Assert.Null(f.Store.Account); Assert.Equal(0, f.Ops.Installs);
    }
    [Fact]
    public async Task ConcurrentRefreshesJoinOneProbe()
    {
        using var f = new Fixture(true, A.Id); f.Ops.ProbeGate = new();
        var first = f.Store.RefreshAsync(); var second = f.Store.RefreshAsync(); Assert.Same(first, second);
        f.Ops.ProbeGate.SetResult(); await first; Assert.Equal(1, f.Ops.Probes); Assert.Equal(1, f.Ops.Installs);
    }
    [Fact]
    public async Task DisableWaitsForLateInstallAndPreventsReinstallation()
    {
        using var f = new Fixture(true, A.Id); f.Ops.InstallGate = new();
        var refresh = f.Store.RefreshAsync();
        await WaitUntil(() => f.Ops.Installs == 1);
        var disable = f.Store.SetEnabledAsync(false);
        Assert.False(f.Store.Available); Assert.Equal(0, f.Ops.Uninstalls);
        await f.Store.RefreshAsync(); Assert.Equal(1, f.Ops.Probes);
        f.Ops.InstallGate.SetResult(); await refresh; Assert.True(await disable);
        Assert.False(f.Ops.Installed); Assert.False(f.Store.Available); Assert.Equal(1, f.Ops.Uninstalls);
    }
    [Fact]
    public async Task SwitchWaitsForInstallAndApprovesOnlyVerifiedResult()
    {
        using var f = new Fixture(true, A.Id); f.Ops.InstallGate = new();
        var refresh = f.Store.RefreshAsync(); await WaitUntil(() => f.Ops.Installs == 1);
        var switching = f.Store.BeginAccountSwitchAsync(); Assert.False(switching.IsCompleted); Assert.False(f.Store.Available);
        f.Ops.InstallGate.SetResult(); await refresh; await switching;
        f.Ops.Current = B; await f.Store.FinishAccountSwitchAsync(null);
        Assert.False(f.Store.Available); Assert.Equal(A.Id, f.Saved.LinkedAccountId);
        await f.Store.BeginAccountSwitchAsync(); await f.Store.FinishAccountSwitchAsync(B.Id);
        Assert.True(f.Store.Available); Assert.Equal(B.Id, f.Saved.LinkedAccountId);
    }
    [Fact]
    public async Task OwnerChangesWhileHelperInstallsCannotPublishOldAccount()
    {
        using var f = new Fixture(true, A.Id); f.Ops.InstallGate = new();
        var refresh = f.Store.RefreshAsync(); await WaitUntil(() => f.Ops.Installs == 1);
        f.Ops.Current = B; f.Ops.InstallGate.SetResult(); await refresh;
        Assert.False(f.Store.Available); Assert.Null(f.Store.Account); Assert.Null(f.Store.DetectedAccount);
    }
    [Fact]
    public async Task DisposeCancelsProbeAndStopsCallbacks()
    {
        using var f = new Fixture(true, A.Id); f.Ops.ProbeGate = new();
        var changed = 0; f.Store.Changed += () => changed++;
        var refresh = f.Store.RefreshAsync(); await WaitUntil(() => f.Ops.Probes == 1);
        f.Store.Dispose(); var before = changed; await refresh;
        Assert.Equal(before, changed); Assert.False(f.Store.Available); Assert.Equal(0, f.Ops.Installs);
    }
    [Fact]
    public async Task SuccessfulSwitchCannotApproveASecondExternalAccount()
    {
        using var f = new Fixture(true, A.Id); await f.Store.RefreshAsync();
        await f.Store.BeginAccountSwitchAsync();
        f.Ops.Current = new("owner-c", "c@example.invalid", "org-c", "pro");
        await f.Store.FinishAccountSwitchAsync(B.Id);
        Assert.False(f.Store.Available); Assert.Equal(A.Id, f.Saved.LinkedAccountId);
        Assert.Null(f.Store.Account); Assert.Equal(1, f.Ops.Installs);
    }
    [Fact]
    public async Task MissingOrFailedProbeDoesNotReportSuccessfulAdd()
    {
        using var f = new Fixture(); f.Ops.Current = null;
        Assert.False(await f.Store.AddCurrentAccountAsync()); Assert.Null(f.Saved.LinkedAccountId);
        f.Ops.Current = A; f.Ops.FailProbe = true;
        Assert.False(await f.Store.AddCurrentAccountAsync()); Assert.Null(f.Saved.LinkedAccountId);
    }
    [Fact]
    public async Task NotificationFailureCannotStrandBusyMutation()
    {
        using var f = new Fixture(true, A.Id); await f.Store.RefreshAsync();
        var first = true;
        f.Store.Changed += () => { if (first) { first = false; throw new IOException("subscriber failed"); } };
        Assert.False(await f.Store.SetEnabledAsync(false)); Assert.False(f.Store.Busy);
        Assert.True(await f.Store.SetEnabledAsync(false)); Assert.False(f.Store.Available);
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
}
