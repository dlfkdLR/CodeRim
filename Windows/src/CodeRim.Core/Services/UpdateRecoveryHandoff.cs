using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;

namespace CodeRim.Core.Services;

internal sealed record UpdateRecoveryRequest(string OperationId, string LauncherId, UpdateParent Parent)
{
    internal static UpdateRecoveryRequest Parse(string text)
    {
        if (text.Length > 1024) throw new InvalidDataException("Invalid recovery request.");
        using var document = JsonDocument.Parse(text); InstallPayloadManifest.ExactObject(document.RootElement, "OperationId", "LauncherId", "Parent");
        var value = document.RootElement.Deserialize<UpdateRecoveryRequest>() ?? throw new InvalidDataException("Missing recovery request.");
        _ = UpdateWorkerInvocation.ForRecovery(value.OperationId); _ = UpdateWorkerInvocation.ForRecovery(value.LauncherId);
        _ = UpdateParent.Parse(document.RootElement.GetProperty(nameof(Parent)).GetRawText()); return value;
    }
}

public static class UpdateRecovery
{
    [SupportedOSPlatform("windows")]
    public static string? LatestPendingOperation()
    {
        var location = WindowsUpdateLocation.Open();
        var directories = Directory.EnumerateDirectories(location.Capsules).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)).Take(9).ToArray();
        if (directories.Length > 8) throw new IOException("Preserved updates need manual review.");
        foreach (var path in directories.OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            InstallFileSystem.AssertPrivateDirectory(path);
            if (!File.Exists(Path.Combine(path, "request.json"))) continue;
            var id = Path.GetFileName(path);
            var results = Directory.EnumerateFiles(location.PrivateDirectory("Results"), id + "-*.json").Take(9).ToArray();
            // These markers only suppress a completed entry in the UI; they never authorize an installation or recovery.
            if (results.Length is > 0 and <= 8 && results.Any(file => UpdateBootstrap.ReadResult(file, id).Status is UpdateExecutionStatus.Applied or UpdateExecutionStatus.RolledBack)) continue;
            return id;
        }
        return null;
    }
    [SupportedOSPlatform("windows")]
    public static async Task<UpdateExecutionResult> PrepareAsync(string operation, string launcherId, Assembly application, CancellationToken token)
    {
        if (!UpdateBootstrap.IsSigningConfigured(application)) return new(UpdateExecutionStatus.SigningNotConfigured);
        var request = UpdateRecoveryRequest.Parse(JsonSerializer.Serialize(new UpdateRecoveryRequest(operation, launcherId, UpdateParent.Current())));
        var location = WindowsUpdateLocation.Open();
        var self = Environment.ProcessPath ?? throw new IOException("Missing application image.");
        if (!string.Equals(InstallFileSystem.LongWindowsPath(self), Path.Combine(location.InstallRoot, "CodeRim.exe"), StringComparison.OrdinalIgnoreCase))
            return new(UpdateExecutionStatus.UnmanagedInstallation, operation);
        InstallFileSystem.AssertPrivateDirectory(location.InstallRoot);
        var launcher = location.Launcher(launcherId, create: true);
        var pin = PublisherPin.FromBuildMetadata(application.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(value => value.Key == "CodeRimPublisherSpkiSha256").Select(value => value.Value))!;
        using var image = await UpdateBootstrapVerification.VerifyAsync(self, pin, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try
        {
            using var process = UpdateBootstrap.StartDetached(self, [UpdateBootstrap.EntryArgument, "--recover", JsonSerializer.Serialize(request)], location.ChildEnvironment());
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(3));
            var resultPath = Path.Combine(launcher, "ready.json");
            while (!File.Exists(resultPath)) await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            var result = UpdateBootstrap.ReadResult(resultPath, operation); token.ThrowIfCancellationRequested(); return result;
        }
        catch (OperationCanceledException)
        {
            try { UpdateBootstrap.WithdrawRestart(operation, launcherId); } catch (Exception cancellation) when (UpdateBootstrap.Expected(cancellation)) { }
            throw;
        }
    }
    [SupportedOSPlatform("windows")]
    internal static void RunBootstrap(string text, Assembly application)
    {
        using var deadline = UpdateChildDeadline.Arm(); var request = UpdateRecoveryRequest.Parse(text);
        var location = WindowsUpdateLocation.Open();
        try
        {
            var pin = PublisherPin.FromBuildMetadata(application.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(value => value.Key == "CodeRimPublisherSpkiSha256").Select(value => value.Value)) ?? throw new InvalidDataException("No publisher is configured.");
            var self = Environment.ProcessPath ?? throw new IOException("Missing application image.");
            if (!string.Equals(InstallFileSystem.LongWindowsPath(self), Path.Combine(location.InstallRoot, "CodeRim.exe"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A managed application is required.");
            using var app = WindowsPublisherTrust.Verify(self, pin);
            using var worker = WindowsPublisherTrust.Verify(Path.Combine(location.InstallRoot, SignedInstallPayload.WorkerName), pin);
            var manifest = SignedInstallPayload.Read(worker); var entry = manifest.Files.Single(file => file.Path == "CodeRim.exe");
            if (app.Size != entry.Size || app.Sha256 != entry.Sha256) throw new InvalidDataException("The installed application does not match.");
            var copy = Path.Combine(location.Launcher(request.LauncherId), SignedInstallPayload.WorkerName);
            SignedInstallPayload.CopyVerified(worker.Path, copy, worker.Size, worker.Sha256);
            using var copied = WindowsPublisherTrust.Verify(copy, pin); location.AssertWorkerOutsideInstallation(copy);
            using var child = UpdateBootstrap.StartDetached(copy, ["handoff-recover", text], location.ChildEnvironment());
        }
        catch (Exception exception) when (UpdateBootstrap.Expected(exception))
        { UpdateBootstrap.WriteResult(Path.Combine(location.Launcher(request.LauncherId), "ready.json"), new(UpdateExecutionStatus.PublisherRejected, request.OperationId)); }
    }
    [SupportedOSPlatform("windows")]
    internal static async Task<UpdateExecutionResult> RunWorkerAsync(string self, string text, PublisherPin pin)
    {
        using var deadline = UpdateChildDeadline.ArmSupervisor(); var request = UpdateRecoveryRequest.Parse(text);
        var location = WindowsUpdateLocation.Open(); location.AssertWorkerOutsideInstallation(self);
        var launcher = location.Launcher(request.LauncherId);
        var result = await UpdateWorkerSupervisor.RunAsync(self, ["validate", request.OperationId], pin, CancellationToken.None).ConfigureAwait(false);
        UpdateBootstrap.WriteResult(Path.Combine(launcher, "ready.json"), result);
        if (result.Status != UpdateExecutionStatus.Prepared) return result;
        if (!await UpdateHandoff.AwaitRestartAsync(launcher, request.OperationId, request.Parent).ConfigureAwait(false)) return new(UpdateExecutionStatus.ParentStillRunning, request.OperationId);
        result = await UpdateWorkerSupervisor.RunAsync(self, ["recover", request.OperationId], pin, CancellationToken.None).ConfigureAwait(false);
        result = UpdateCapsuleCleanup.Record(location, result);
        return result; // Recovery does not silently relaunch if a pending journal could not be classified.
    }
}
