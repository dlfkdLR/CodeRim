using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace CodeRim.Core.Services;

[SupportedOSPlatform("windows")]
internal static class WindowsInitialInstall
{
    // This is an explicitly launched signed installer's bootstrap, never a downloaded automatic-update verifier.
    internal static async Task<UpdateExecutionResult> BootstrapAsync(string self, string source, PublisherPin pin, bool recover = false)
    {
        using var deadline = UpdateChildDeadline.ArmSupervisor();
        using var identity = WindowsPublisherTrust.Verify(self, pin);
        var location = WindowsUpdateLocation.Open();
        if (!recover && Path.Exists(location.InstallRoot)) return new(UpdateExecutionStatus.UnmanagedInstallation);
        var id = recover ? UpdateWorkerInvocation.ForRecovery(source).OperationId : Guid.NewGuid().ToString("N");
        var launcher = location.Launcher(Guid.NewGuid().ToString("N"), create: true);
        var copy = Path.Combine(launcher, SignedInstallPayload.WorkerName);
        SignedInstallPayload.CopyVerified(self, copy, identity.Size, identity.Sha256);
        using var copied = WindowsPublisherTrust.Verify(copy, pin); location.AssertWorkerOutsideInstallation(copy);
        var result = await UpdateWorkerSupervisor.RunAsync(copy, recover ? ["recover-install", id] : ["install", source, id], pin, CancellationToken.None).ConfigureAwait(false);
        return UpdateCapsuleCleanup.Record(location, result);
    }
    internal static UpdateExecutionResult Execute(string self, string[] args, PublisherPin pin)
    {
        using var deadline = UpdateChildDeadline.Arm();
        var recovery = args.Length == 2 && args[0] == "recover-install";
        if (!recovery && (args.Length != 3 || args[0] != "install")) return new(UpdateExecutionStatus.InvalidRequest);
        var id = UpdateWorkerInvocation.ForRecovery(recovery ? args[1] : args[2]).OperationId;
        var location = WindowsUpdateLocation.Open(); location.AssertWorkerOutsideInstallation(self);
        var capsule = location.Capsule(id); InstallPayloadManifest? manifest = null; var attempted = false; UpdatePreparation? preparation = null;
        try
        {
            using var selfIdentity = WindowsPublisherTrust.Verify(self, pin);
            if (!recovery)
            {
                if (Path.Exists(location.InstallRoot)) return new(UpdateExecutionStatus.UnmanagedInstallation, id);
                InstallFileSystem.CreatePrivateDirectory(capsule);
                preparation = new(capsule); preparation.Copy(self, "next-worker.exe", selfIdentity.Size, selfIdentity.Sha256);
            }
            using var worker = WindowsPublisherTrust.Verify(Path.Combine(capsule, "next-worker.exe"), pin);
            manifest = SignedInstallPayload.Read(worker);
            var architecture = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => "unsupported" };
            if (manifest.Architecture != architecture) throw new InvalidDataException("The signed payload architecture does not match.");
            var archive = Path.Combine(capsule, "package.zip");
            if (!recovery)
            {
                var source = args[1]; if (!Path.IsPathFullyQualified(source)) throw new InvalidDataException("An absolute extracted package is required.");
                manifest.VerifyUninstalledTree(source, CancellationToken.None);
                using var app = WindowsPublisherTrust.Verify(Path.Combine(source, "CodeRim.exe"), pin);
                var entry = manifest.Files.Single(file => file.Path == "CodeRim.exe");
                if (app.Size != entry.Size || app.Sha256 != entry.Sha256) throw new InvalidDataException("The signed application does not match.");
                preparation!.Write("package.zip", output =>
                {
                    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                        foreach (var file in manifest.Files)
                        {
                            var path = InstallPayloadManifest.FullPath(source, file.Path); InstallFileSystem.CheckPath(path); InstallFileSystem.CheckStreams(path);
                            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            if (input.Length != file.Size) throw new InvalidDataException("The extracted package changed.");
                            using var target = zip.CreateEntry(file.Path, CompressionLevel.NoCompression).Open();
                            var buffer = new byte[65536]; long length = 0; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); int count;
                            while ((count = input.Read(buffer)) > 0)
                            {
                                length += count; if (length > file.Size) throw new InvalidDataException("The extracted package grew.");
                                hash.AppendData(buffer, 0, count); target.Write(buffer, 0, count);
                            }
                            if (length != file.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256) throw new InvalidDataException("The extracted package changed.");
                        }
                    output.Flush(true);
                });
                using var retainedPackage = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
                var receipt = UpdateCapsuleCleanup.InitialReceiptBytes(manifest,
                    [new("next-worker.exe", worker.Size, worker.Sha256), new("package.zip", retainedPackage.Length, Convert.ToHexStringLower(SHA256.HashData(retainedPackage)))]);
                preparation.Write(InstallPayloadManifest.ReceiptName, output => output.Write(receipt));
            }
            preparation?.KeepForRecovery();
            attempted = true;
            if (recovery)
            {
                var state = InstallTransaction.RecoverAuthenticated(location.InstallRoot, manifest, null);
                if (state == InstallRecoveryResult.RolledBack || state == InstallRecoveryResult.None && !Path.Exists(location.InstallRoot)) return new(UpdateExecutionStatus.RolledBack, id);
            }
            else InstallTransaction.ApplyAuthenticated(location.InstallRoot, archive, manifest, null);
            manifest.VerifyTree(location.InstallRoot, CancellationToken.None); return new(UpdateExecutionStatus.Applied, id);
        }
        catch (Exception exception) when (UpdateBootstrap.Expected(exception) || exception is AggregateException)
        {
            if (!attempted) return new(UpdateExecutionStatus.PackageRejected, id);
            try { manifest!.VerifyTree(location.InstallRoot, CancellationToken.None); return new(UpdateExecutionStatus.AppliedCleanupPending, id); }
            catch (Exception verification) when (UpdateBootstrap.Expected(verification)) { }
            try
            {
                _ = InstallTransaction.RecoverAuthenticated(location.InstallRoot, manifest!, null);
                if (!Path.Exists(location.InstallRoot)) return new(UpdateExecutionStatus.RolledBack, id);
            }
            catch (Exception recoveryFailure) when (UpdateBootstrap.Expected(recoveryFailure) || recoveryFailure is AggregateException) { }
            return new(UpdateExecutionStatus.RecoveryRequired, id);
        }
        finally { preparation?.Dispose(); }
    }
}
