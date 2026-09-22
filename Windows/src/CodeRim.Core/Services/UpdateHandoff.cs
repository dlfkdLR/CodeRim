using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace CodeRim.Core.Services;

// Fixed, bounded IPC contains release/process identities only. No credential/profile environment is inherited.
internal sealed record UpdateParent(int Id, long StartedUtcTicks)
{
    internal static UpdateParent Current() { using var process = Process.GetCurrentProcess(); return new(process.Id, process.StartTime.ToUniversalTime().Ticks); }
    internal static UpdateParent Parse(string json)
    {
        if (json.Length > 256) throw new InvalidDataException("Invalid update parent.");
        using var document = JsonDocument.Parse(json); InstallPayloadManifest.ExactObject(document.RootElement, "Id", "StartedUtcTicks");
        var value = document.RootElement.Deserialize<UpdateParent>() ?? throw new InvalidDataException("Missing update parent.");
        if (value.Id <= 0 || value.StartedUtcTicks <= 0 || value.StartedUtcTicks > DateTime.MaxValue.Ticks) throw new InvalidDataException("Invalid update parent.");
        return value;
    }
    internal async Task<bool> WaitAsync(TimeSpan budget, CancellationToken token)
    {
        try
        {
            using var process = Process.GetProcessById(Id);
            if (process.StartTime.ToUniversalTime().Ticks != StartedUtcTicks) return true; // Original process exited; never wait for a reused PID.
            try { await process.WaitForExitAsync(token).WaitAsync(budget, token).ConfigureAwait(false); return true; }
            catch (TimeoutException) { return false; }
        }
        catch (ArgumentException) { return true; }
    }
}

public static class UpdateBootstrap
{
    public const string EntryArgument = "--update-bootstrap";
    // Calling assembly is explicit so the UI cannot accidentally read Core's metadata. Metadata alone never authorizes installation.
    public static bool IsSigningConfigured(Assembly application)
    {
        try { return ReadPin(application) is not null; }
        catch (InvalidDataException) { return false; }
    }
    private static PublisherPin? ReadPin(Assembly application) => PublisherPin.FromBuildMetadata(application.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Where(attribute => attribute.Key == "CodeRimPublisherSpkiSha256").Select(attribute => attribute.Value));

    [SupportedOSPlatform("windows")]
    public static string DownloadDirectory() => WindowsUpdateLocation.Open().PrivateDirectory("Downloads");

    [SupportedOSPlatform("windows")]
    public static async Task<UpdateExecutionResult> PrepareAsync(DownloadedReleasePackage downloaded, Assembly application, CancellationToken token)
    {
        if (!IsSigningConfigured(application)) return new(UpdateExecutionStatus.SigningNotConfigured);
        var invocation = UpdateWorkerInvocation.ForDownloadedPackage(downloaded);
        try
        {
            token.ThrowIfCancellationRequested();
            var location = WindowsUpdateLocation.Open(); var self = Environment.ProcessPath ?? throw new IOException("Missing application image.");
            if (!string.Equals(InstallFileSystem.LongWindowsPath(self), Path.Combine(location.InstallRoot, "CodeRim.exe"), StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(location.InstallRoot, InstallPayloadManifest.ReceiptName))) return new(UpdateExecutionStatus.UnmanagedInstallation, invocation.OperationId);
            InstallFileSystem.AssertPrivateDirectory(location.InstallRoot);
            var launcher = location.Launcher(invocation.OperationId, create: true);
            // Re-execute only the already-running application. It branches before normal app startup, authenticates the installed worker,
            // and copies that worker out of the installation. Downloaded code is never used as a bootstrap verifier.
            using var lease = await UpdateBootstrapVerification.VerifyAsync(self, ReadPin(application)!, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            using var process = StartDetached(self, [EntryArgument, invocation.Arguments[1], downloaded.Path, JsonSerializer.Serialize(UpdateParent.Current())], location.ChildEnvironment());
            var path = Path.Combine(launcher, "ready.json");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(3));
            while (!File.Exists(path))
            {
                deadline.Token.ThrowIfCancellationRequested();
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
            var result = ReadResult(path, invocation.OperationId);
            if (result.Status != UpdateExecutionStatus.Prepared) return result;
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException)
        {
            try { WithdrawRestart(invocation.OperationId, invocation.OperationId); } catch (Exception cancellation) when (Expected(cancellation)) { }
            return new(UpdateExecutionStatus.CancelledBeforeStart, invocation.OperationId);
        }
        catch (TimeoutException) { return new(UpdateExecutionStatus.PublisherRejected, invocation.OperationId); }
        catch (Exception exception) when (Expected(exception)) { return new(UpdateExecutionStatus.UnsafeLocation, invocation.OperationId); }
    }

    [SupportedOSPlatform("windows")]
    public static Task ConfirmRestartAsync(string operation, CancellationToken token) => ConfirmRestartAsync(operation, operation, token);
    [SupportedOSPlatform("windows")]
    public static async Task ConfirmRestartAsync(string operation, string launcherId, CancellationToken token)
    {
        var location = WindowsUpdateLocation.Open(); var launcher = location.Launcher(launcherId);
        if (ReadResult(Path.Combine(launcher, "ready.json"), operation).Status != UpdateExecutionStatus.Prepared)
            throw new InvalidDataException("The update has not been prepared.");
        await UpdateRestartHandshake.ConfirmAsync(launcher, operation, token).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    public static void WithdrawRestart(string operation, string launcherId)
    {
        _ = UpdateWorkerInvocation.ForRecovery(operation);
        var location = WindowsUpdateLocation.Open(); var launcher = location.Launcher(launcherId);
        var path = Path.Combine(launcher, "restart.confirmed"); InstallFileSystem.CheckPath(path);
        if (File.Exists(path)) File.Delete(path);
        var cancelled = Path.Combine(launcher, "restart.cancelled");
        if (!File.Exists(cancelled)) InstallFileSystem.WriteNew(cancelled, System.Text.Encoding.ASCII.GetBytes(operation));
    }

    // Called exclusively before WPF creates settings/vault/store. The ordinary bootstrap never mutates an installation.
    [SupportedOSPlatform("windows")]
    public static Task RunEntryAsync(string[] args, Assembly application)
    {
        using var deadline = UpdateChildDeadline.Arm();
        UpdateWorkerRequest? request = null; WindowsUpdateLocation? location = null;
        try
        {
            if (args.Length == 3 && args[0] == EntryArgument && args[1] == "--recover")
            { UpdateRecovery.RunBootstrap(args[2], application); return Task.CompletedTask; }
            if (args.Length != 4 || args[0] != EntryArgument) return Task.CompletedTask;
            request = UpdateWorkerRequest.Parse(args[1]); var parent = UpdateParent.Parse(args[3]);
            var pin = ReadPin(application); if (pin is null) return Task.CompletedTask;
            location = WindowsUpdateLocation.Open(); var launcher = location.Launcher(request.OperationId);
            var self = Environment.ProcessPath ?? throw new IOException("Missing application image.");
            if (!string.Equals(InstallFileSystem.LongWindowsPath(self), Path.Combine(location.InstallRoot, "CodeRim.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A managed application is required.");
            using var app = WindowsPublisherTrust.Verify(self, pin);
            using var worker = WindowsPublisherTrust.Verify(Path.Combine(location.InstallRoot, SignedInstallPayload.WorkerName), pin);
            var manifest = SignedInstallPayload.Read(worker); var entry = manifest.Files.Single(file => file.Path == "CodeRim.exe");
            if (entry.Size != app.Size || entry.Sha256 != app.Sha256) throw new InvalidDataException("The installed application does not match its signed manifest.");
            var copy = Path.Combine(launcher, SignedInstallPayload.WorkerName); SignedInstallPayload.CopyVerified(worker.Path, copy, worker.Size, worker.Sha256);
            using var copied = WindowsPublisherTrust.Verify(copy, pin); location.AssertWorkerOutsideInstallation(copy);
            using var child = StartDetached(copy, ["handoff", args[1], args[2], JsonSerializer.Serialize(parent)], location.ChildEnvironment());
        }
        catch (Exception exception) when (Expected(exception))
        {
            if (request is not null && location is not null)
                try { WriteResult(Path.Combine(location.Launcher(request.OperationId), "ready.json"), new(UpdateExecutionStatus.PublisherRejected, request.OperationId)); }
                catch (Exception writing) when (Expected(writing)) { }
        }
        return Task.CompletedTask;
    }
    [SupportedOSPlatform("windows")]
    internal static Process StartDetached(string executable, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?> environment)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.Environment.Clear(); foreach (var item in environment) start.Environment[item.Key] = item.Value;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // This supervisor must survive the UI's shutdown. Its transaction child has its own kill-on-close job and deadline.
        lock (BoundedProcess.CreationLock) return Process.Start(start) ?? throw new IOException("The update supervisor could not start.");
    }
    internal static UpdateExecutionResult ReadResult(string path, string operation)
    {
        var result = UpdateWorkerSupervisor.ParseResult(new UTF8Encoding(false, true).GetString(InstallFileSystem.ReadBounded(path, UpdateWorkerSupervisor.MaximumOutput)));
        if (result.OperationId != operation) throw new InvalidDataException("The update result identifies another operation.");
        return result;
    }
    internal static void WriteResult(string path, UpdateExecutionResult result) => InstallFileSystem.WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(new { result.Status, result.OperationId }));
    internal static bool Expected(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
        or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception or JsonException;
}

[SupportedOSPlatform("windows")]
internal static class UpdateHandoff
{
    internal static async Task<UpdateExecutionResult> RunAsync(string self, string[] args, PublisherPin pin)
    {
        using var deadline = UpdateChildDeadline.ArmSupervisor();
        if (args.Length != 4 || args[0] != "handoff") return new(UpdateExecutionStatus.InvalidRequest);
        var request = UpdateWorkerRequest.Parse(args[1]); var parent = UpdateParent.Parse(args[3]);
        var location = WindowsUpdateLocation.Open(); location.AssertWorkerOutsideInstallation(self);
        using var identity = WindowsPublisherTrust.Verify(self, pin);
        var launcher = location.Launcher(request.OperationId);
        var result = await UpdateWorkerSupervisor.RunAsync(self, ["prepare", args[1], args[2]], pin, CancellationToken.None).ConfigureAwait(false);
        UpdateBootstrap.WriteResult(Path.Combine(launcher, "ready.json"), result);
        if (result.Status != UpdateExecutionStatus.Prepared) return result;
        if (!await AwaitRestartAsync(launcher, request.OperationId, parent).ConfigureAwait(false))
            return UpdateCapsuleCleanup.AbandonPrepared(location, new(UpdateExecutionStatus.ParentStillRunning, request.OperationId));
        result = await UpdateWorkerSupervisor.RunAsync(self, ["commit", request.OperationId], pin, CancellationToken.None).ConfigureAwait(false);
        result = UpdateCapsuleCleanup.Record(location, result);
        if (result.Status is UpdateExecutionStatus.Applied or UpdateExecutionStatus.AppliedCleanupPending or UpdateExecutionStatus.RolledBack)
        {
            using var installed = WindowsPublisherTrust.Verify(Path.Combine(location.InstallRoot, "CodeRim.exe"), pin);
            using var installedWorker = WindowsPublisherTrust.Verify(Path.Combine(location.InstallRoot, SignedInstallPayload.WorkerName), pin);
            var manifest = SignedInstallPayload.Read(installedWorker); var entry = manifest.Files.Single(file => file.Path == "CodeRim.exe");
            if (entry.Size == installed.Size && entry.Sha256 == installed.Sha256)
            {
                WindowsApplicationRestart.Start(installed.Path, location.InstallRoot);
            }
        }
        return result;
    }
    internal static Task<bool> AwaitRestartAsync(string launcher, string operation, UpdateParent parent)
        => UpdateRestartHandshake.WaitAsync(launcher, operation, parent, TimeSpan.FromMinutes(2), CancellationToken.None);

}
