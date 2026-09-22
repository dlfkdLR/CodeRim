using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace CodeRim.Core.Services;

// The worker supervises a copy of its already-running executable; it never executes downloaded candidate bytes.
// Each re-execution is authenticated before launch. Payload verification and transaction I/O run in the contained child.
internal static class UpdateWorkerSupervisor
{
    internal const int MaximumOutput = 8192;
    private static readonly string[] ChildArgument = ["--child"];
    [SupportedOSPlatform("windows")]
    internal static async Task<UpdateExecutionResult> RunAsync(string self, string[] arguments, PublisherPin pin, CancellationToken token)
    {
        using var ownDeadline = UpdateChildDeadline.ArmSupervisor();
        if (arguments.Length is not (2 or 3) || arguments[0] is not ("apply" or "prepare" or "commit" or "recover" or "install" or "recover-install" or "validate")) return new(UpdateExecutionStatus.InvalidRequest);
        string operation;
        try
        {
            operation = arguments[0] is "apply" or "prepare" && arguments.Length == 3 ? UpdateWorkerRequest.Parse(arguments[1]).OperationId
                : arguments[0] is "recover" or "commit" or "recover-install" or "validate" && arguments.Length == 2 ? UpdateWorkerInvocation.ForRecovery(arguments[1]).OperationId
                : arguments[0] == "install" && arguments.Length == 3 ? UpdateWorkerInvocation.ForRecovery(arguments[2]).OperationId
                : throw new InvalidDataException("Invalid update invocation.");
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException) { return new(UpdateExecutionStatus.InvalidRequest); }
        var location = WindowsUpdateLocation.Open(); location.AssertWorkerOutsideInstallation(self);
        // Verify the path before executing it again; an already-running image does not authenticate replacement bytes at its path.
        // The supervisor's own watchdog bounds this native check. Retain the verified lease through child exit.
        using var imageLease = WindowsPublisherTrust.Verify(self, pin);
        var environment = location.ChildEnvironment();
        var result = await SuperviseAsync(() => new NativeChild(WindowsIsolatedProcess.Start(self, ChildArgument.Concat(arguments),
            environment)), TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
        return BindResult(result, operation);
    }
    internal static UpdateExecutionResult BindResult(UpdateExecutionResult result, string operation) =>
        result.OperationId is not null && result.OperationId != operation ? new(UpdateExecutionStatus.RecoveryRequired, operation) : result with { OperationId = operation };

    internal interface IChild : IDisposable
    {
        Stream Output { get; }
        Stream Error { get; }
        Task<int> Exit { get; }
        void Kill();
    }
    [SupportedOSPlatform("windows")]
    private sealed class NativeChild : IChild
    {
        private readonly WindowsIsolatedProcess process;
        internal NativeChild(WindowsIsolatedProcess process) { this.process = process; Exit = process.WaitAsync(); }
        public Stream Output => process.Output; public Stream Error => process.Error; public Task<int> Exit { get; }
        public void Kill() => process.KillTree(); public void Dispose() => process.Dispose();
    }
    // This injectable supervision seam cannot authorize files or invoke the transaction engine.
    internal static async Task<UpdateExecutionResult> SuperviseAsync(Func<IChild> start, TimeSpan budget, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(UpdateExecutionStatus.CancelledBeforeStart);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(budget);
        var starting = Task.Run(start, CancellationToken.None); IChild? child = null; Task? work = null;
        try
        {
            child = await starting.WaitAsync(deadline.Token).ConfigureAwait(false);
            var output = Read(child.Output, deadline.Token); var error = Read(child.Error, deadline.Token);
            work = Task.WhenAll(output, error, child.Exit);
            await work.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (child.Exit.Result != 0 || error.Result.Length != 0) return new(UpdateExecutionStatus.RecoveryRequired);
            return ParseResult(output.Result);
        }
        catch (OperationCanceledException)
        { return new(token.IsCancellationRequested ? UpdateExecutionStatus.CancelledRecoveryRequired : UpdateExecutionStatus.TimedOutRecoveryRequired); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException or ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new(UpdateExecutionStatus.RecoveryRequired); }
        finally
        {
            deadline.Cancel();
            if (child is not null)
            {
                child.Kill(); var owned = child;
                // Do not let a stuck kernel wait or stream cleanup extend the caller's deadline.
                _ = Task.Run(async () => { try { if (work is not null) await work.ConfigureAwait(false); else await owned.Exit.ConfigureAwait(false); } catch { } finally { owned.Dispose(); } }, CancellationToken.None);
            }
            else _ = starting.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) { task.Result.Kill(); task.Result.Dispose(); } else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    private static async Task<string> Read(Stream stream, CancellationToken token)
    {
        var bytes = new byte[MaximumOutput + 1]; var length = 0;
        while (true)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false); if (count == 0) break;
            length += count; if (length > MaximumOutput) throw new InvalidDataException("Update worker output exceeded its limit.");
        }
        return new UTF8Encoding(false, true).GetString(bytes, 0, length);
    }
    internal static UpdateExecutionResult ParseResult(string text)
    {
        if (text.Length > MaximumOutput) throw new InvalidDataException("Invalid worker response.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
        InstallPayloadManifest.ExactObject(document.RootElement, "Status", "OperationId");
        var status = document.RootElement.GetProperty("Status"); var operation = document.RootElement.GetProperty("OperationId");
        if (!status.TryGetInt32(out var number) || !Enum.IsDefined((UpdateExecutionStatus)number) || operation.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new InvalidDataException("Invalid update result.");
        var id = operation.GetString();
        if (id is not null && (!Guid.TryParseExact(id, "N", out var guid) || guid.ToString("N") != id)) throw new InvalidDataException("Invalid update operation.");
        if ((UpdateExecutionStatus)number is UpdateExecutionStatus.Applied or UpdateExecutionStatus.AppliedCleanupPending or UpdateExecutionStatus.RolledBack or UpdateExecutionStatus.Prepared && id is null)
            throw new InvalidDataException("Successful update evidence must identify its operation.");
        return new((UpdateExecutionStatus)number, id);
    }
}
