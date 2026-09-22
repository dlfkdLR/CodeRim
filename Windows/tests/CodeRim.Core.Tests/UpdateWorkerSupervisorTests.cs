using System.Diagnostics;
using System.Text;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class UpdateWorkerSupervisorTests
{
    [Fact]
    public async Task CompletedEvidencePreservesCommittedCleanupState()
    {
        using var child = new Child("{\"Status\":1,\"OperationId\":\"0123456789abcdef0123456789abcdef\"}");
        var result = await UpdateWorkerSupervisor.SuperviseAsync(() => child, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(UpdateExecutionStatus.AppliedCleanupPending, result.Status);
    }
    [Fact]
    public async Task CancellationBeforeStartDoesNotLaunchAnything()
    {
        var started = false; using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await UpdateWorkerSupervisor.SuperviseAsync(() => { started = true; return new Child(""); }, TimeSpan.FromSeconds(1), cancel.Token);
        Assert.False(started); Assert.Equal(UpdateExecutionStatus.CancelledBeforeStart, result.Status);
    }
    [Fact]
    public async Task NonCooperativeNativeWaitIsBoundedAndNeverReportedAsRollback()
    {
        using var child = new Child("", new TaskCompletionSource<int>().Task); var watch = Stopwatch.StartNew();
        var result = await UpdateWorkerSupervisor.SuperviseAsync(() => child, TimeSpan.FromMilliseconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(UpdateExecutionStatus.TimedOutRecoveryRequired, result.Status); Assert.True(child.Killed); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task LateProcessCreationIsKilledWithoutExtendingCallerDeadline()
    {
        using var child = new Child(""); using var released = new ManualResetEventSlim();
        var result = await UpdateWorkerSupervisor.SuperviseAsync(() => { released.Wait(); return child; }, TimeSpan.FromMilliseconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(UpdateExecutionStatus.TimedOutRecoveryRequired, result.Status); released.Set();
        Assert.True(SpinWait.SpinUntil(() => child.Killed, TimeSpan.FromSeconds(2)));
    }
    [Theory]
    [InlineData("not json", "")][InlineData("{}", "")][InlineData("{\"Status\":0,\"OperationId\":null}", "")]
    [InlineData("{\"Status\":6,\"OperationId\":null}", "native error")]
    public async Task MalformedOrErrorOutputCannotCreateSuccess(string stdout, string stderr)
    {
        using var child = new Child(stdout, error: stderr);
        var result = await UpdateWorkerSupervisor.SuperviseAsync(() => child, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(UpdateExecutionStatus.RecoveryRequired, result.Status);
    }
    [Fact]
    public async Task OversizedOutputFailsClosed()
    {
        using var child = new Child(new string('x', UpdateWorkerSupervisor.MaximumOutput + 1));
        var result = await UpdateWorkerSupervisor.SuperviseAsync(() => child, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(UpdateExecutionStatus.RecoveryRequired, result.Status);
    }
    [Fact]
    public async Task CancellationAfterLaunchRequiresRecovery()
    {
        using var child = new Child("", new TaskCompletionSource<int>().Task); using var cancel = new CancellationTokenSource();
        var task = UpdateWorkerSupervisor.SuperviseAsync(() => { cancel.CancelAfter(40); return child; }, TimeSpan.FromSeconds(5), cancel.Token);
        var result = await task; Assert.Equal(UpdateExecutionStatus.CancelledRecoveryRequired, result.Status); Assert.True(child.Killed);
    }
    [Fact]
    public async Task InvalidUtf8ResponseCannotAuthorizeSuccess()
    {
        using var child = new Child("x"); child.Output.Position = 0; child.Output.WriteByte(0xff); child.Output.Position = 0;
        var result = await UpdateWorkerSupervisor.SuperviseAsync(() => child, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(UpdateExecutionStatus.RecoveryRequired, result.Status);
    }
    [Fact]
    public void ChildWatchdogSignalsEvenWithoutAParentSupervisor()
    {
        using var fired = new ManualResetEventSlim();
        using var deadline = new UpdateChildDeadline(TimeSpan.FromMilliseconds(40), fired.Set);
        Assert.True(fired.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }
    [Fact]
    public void CompletingBeforeDeadlineCancelsTerminationSignal()
    {
        var fired = false;
        using (var deadline = new UpdateChildDeadline(TimeSpan.FromMinutes(1), () => fired = true)) { }
        Assert.False(fired);
    }
    [Fact]
    public void ChildDeadlineCannotBeExtendedBeyondProductionLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdateChildDeadline(TimeSpan.FromMinutes(3), () => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdateChildDeadline(TimeSpan.Zero, () => { }));
    }
    private sealed class Child : UpdateWorkerSupervisor.IChild
    {
        internal bool Killed;
        internal Child(string output, Task<int>? exit = null, string error = "") { Output = new MemoryStream(Encoding.UTF8.GetBytes(output)); Error = new MemoryStream(Encoding.UTF8.GetBytes(error)); Exit = exit ?? Task.FromResult(0); }
        public Stream Output { get; } public Stream Error { get; } public Task<int> Exit { get; }
        public void Kill() => Killed = true; public void Dispose() { Output.Dispose(); Error.Dispose(); }
    }
}
