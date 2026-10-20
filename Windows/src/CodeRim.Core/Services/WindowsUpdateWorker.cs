using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeRim.Core.Services;

// Only the supervised child invokes this entry point. No credentials, profiles, settings, PATH, registry or application startup.
[SupportedOSPlatform("windows")]
internal static class WindowsUpdateWorker
{
    internal static UpdateExecutionResult Execute(string[] arguments, PublisherPin pin, string self)
    {
        // --child may be invoked from another existing job. Its deadline must not depend solely on our usual supervisor.
        using var deadline = UpdateChildDeadline.Arm();
        return ExecuteCore(arguments, pin, self);
    }
    private static UpdateExecutionResult ExecuteCore(string[] arguments, PublisherPin pin, string self)
    {
        string? operation = null; var attemptedTransaction = false;
        WindowsUpdateLocation? location = null; InstallPayloadManifest? previous = null; InstallPayloadManifest? next = null;
        try
        {
            if (arguments.Length is not (2 or 3) || arguments[0] is not ("apply" or "prepare" or "commit" or "recover" or "validate")) return new(UpdateExecutionStatus.InvalidRequest);
            var apply = arguments[0] is "apply" or "prepare";
            var commit = arguments[0] is "apply" or "commit";
            if (apply != (arguments.Length == 3)) return new(UpdateExecutionStatus.InvalidRequest);
            UpdateWorkerRequest? request = apply ? UpdateWorkerRequest.Parse(arguments[1]) : null;
            operation = request?.OperationId ?? arguments[1];
            if (!Guid.TryParseExact(operation, "N", out var id) || id.ToString("N") != operation) return new(UpdateExecutionStatus.InvalidRequest);
            // Self must be authenticated before any install-related directory is created.
            try { using var verifiedSelf = VerifyPublisher(self, pin); }
            catch (Exception exception) when (IsExpected(exception)) { return new(UpdateExecutionStatus.PublisherRejected, operation); }
            try { location = WindowsUpdateLocation.Open(); location.AssertWorkerOutsideInstallation(self); }
            catch (Exception exception) when (IsExpected(exception)) { return new(UpdateExecutionStatus.UnsafeLocation, operation); }
            UpdateCapsuleCleanup.CollectCompleted(location);
            var capsule = location.Capsule(operation);
            using var preparation = apply ? new UpdatePreparationScope(capsule) : null;
            if (apply)
            {
                if (!Directory.Exists(location.InstallRoot) || !File.Exists(Path.Combine(location.InstallRoot, SignedInstallPayload.WorkerName))
                    || !File.Exists(Path.Combine(location.InstallRoot, InstallPayloadManifest.ReceiptName))) return new(UpdateExecutionStatus.UnmanagedInstallation, operation);
                if (Directory.EnumerateDirectories(location.Capsules).Count(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)) >= 8) return new(UpdateExecutionStatus.RecoveryRequired, operation);
                preparation!.Create();
                using (var installedWorker = VerifyPublisher(Path.Combine(location.InstallRoot, SignedInstallPayload.WorkerName), pin))
                using (var installedApp = VerifyPublisher(Path.Combine(location.InstallRoot, "CodeRim.exe"), pin))
                {
                    previous = SignedInstallPayload.Read(installedWorker); AssertIdentity(previous, "CodeRim.exe", installedApp);
                    InstallFileSystem.AssertPrivateDirectory(location.InstallRoot); previous.VerifyTree(location.InstallRoot, CancellationToken.None);
                    preparation!.Files.Copy(installedWorker.Path, "previous-worker.exe", installedWorker.Size, installedWorker.Sha256);
                    preparation!.Files.Copy(installedApp.Path, "previous-app.exe", installedApp.Size, installedApp.Sha256);
                } // Release installed image leases before directory renames.
                if (!Path.IsPathFullyQualified(arguments[2])) throw new InvalidDataException("An absolute downloaded archive path is required.");
                preparation!.Files.Copy(arguments[2], "package.zip", request!.Size, request.Sha256);
                preparation!.Files.Write("next-worker.exe", output => SignedInstallPayload.ExtractIdentityTo(Path.Combine(capsule, "package.zip"), SignedInstallPayload.WorkerName, output));
                preparation!.Files.Write("request.json", output => output.Write(JsonSerializer.SerializeToUtf8Bytes(request)));
            }
            else
            {
                InstallFileSystem.AssertPrivateDirectory(capsule);
                request = UpdateWorkerRequest.Parse(new UTF8Encoding(false, true).GetString(InstallFileSystem.ReadBounded(Path.Combine(capsule, "request.json"), 4096)));
                if (request.OperationId != operation) throw new InvalidDataException("Update operation does not match.");
            }
            // Reauthenticate retained signed identities on every recovery. Neither the journal nor request JSON authorizes bytes.
            using var oldWorker = VerifyPublisher(Path.Combine(capsule, "previous-worker.exe"), pin);
            using var oldApp = VerifyPublisher(Path.Combine(capsule, "previous-app.exe"), pin);
            using var newWorker = VerifyPublisher(Path.Combine(capsule, "next-worker.exe"), pin);
            previous = SignedInstallPayload.Read(oldWorker); AssertIdentity(previous, "CodeRim.exe", oldApp);
            next = SignedInstallPayload.Read(newWorker);
            var architecture = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => "unsupported" };
            if (next.Version.ToString(3) != request!.Version || next.Architecture != request.Architecture || next.Architecture != architecture
                || previous.Architecture != next.Architecture || previous.Version >= next.Version) throw new InvalidDataException("Signed version or architecture does not match the release.");
            // Hold the exact archive against replacement through extraction/commit/recovery.
            using var archive = new FileStream(Path.Combine(capsule, "package.zip"), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (archive.Length != request.Size || Convert.ToHexStringLower(SHA256.HashData(archive)) != request.Sha256) throw new InvalidDataException("Retained package does not match its request.");
            var newAppPath = Path.Combine(capsule, "next-app.exe");
            if (apply) preparation!.Files.Write("next-app.exe", output => SignedInstallPayload.ExtractIdentityTo(archive.Name, "CodeRim.exe", output, next.Files.Single(file => file.Path == "CodeRim.exe")));
            using var newApp = VerifyPublisher(newAppPath, pin); AssertIdentity(next, "CodeRim.exe", newApp);
            // The worker extracted before its signed manifest was known must also equal the signed package's self-bound entry.
            AssertIdentity(next, SignedInstallPayload.WorkerName, newWorker);
            if (apply)
            {
                var serialized = JsonSerializer.SerializeToUtf8Bytes(request);
                var receipt = UpdateCapsuleCleanup.ReceiptBytes(request,
                [new("previous-worker.exe", oldWorker.Size, oldWorker.Sha256), new("previous-app.exe", oldApp.Size, oldApp.Sha256),
                 new("next-worker.exe", newWorker.Size, newWorker.Sha256), new("next-app.exe", newApp.Size, newApp.Sha256),
                 new("package.zip", request.Size, request.Sha256), new("request.json", serialized.Length, Convert.ToHexStringLower(SHA256.HashData(serialized)))]);
                preparation!.Files.Write(InstallPayloadManifest.ReceiptName, output => output.Write(receipt));
            }
            preparation?.Files.KeepForRecovery();
            if (arguments[0] is "prepare" or "validate") return new(UpdateExecutionStatus.Prepared, operation);
            attemptedTransaction = true;
            if (commit) InstallTransaction.ApplyAuthenticated(location.InstallRoot, archive.Name, next, previous);
            else
            {
                var recovered = InstallTransaction.RecoverAuthenticated(location.InstallRoot, next, previous);
                if (recovered == InstallRecoveryResult.RolledBack) return new(UpdateExecutionStatus.RolledBack, operation);
                if (recovered == InstallRecoveryResult.None)
                {
                    try { previous.VerifyTree(location.InstallRoot, CancellationToken.None); return new(UpdateExecutionStatus.RolledBack, operation); }
                    catch (Exception exception) when (IsExpected(exception)) { }
                }
            }
            next.VerifyTree(location.InstallRoot, CancellationToken.None);
            return new(UpdateExecutionStatus.Applied, operation);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            if (!attemptedTransaction) return new(exception is PublisherValidationException ? UpdateExecutionStatus.PublisherRejected : UpdateExecutionStatus.PackageRejected, operation);
            // Never call every exception a rollback: cleanup may fail after the committed rename.
            try { next!.VerifyTree(location!.InstallRoot, CancellationToken.None); return new(UpdateExecutionStatus.AppliedCleanupPending, operation); }
            catch (Exception verification) when (IsExpected(verification)) { }
            try
            {
                _ = InstallTransaction.RecoverAuthenticated(location!.InstallRoot, next!, previous!);
                previous!.VerifyTree(location.InstallRoot, CancellationToken.None); return new(UpdateExecutionStatus.RolledBack, operation);
            }
            catch (Exception verification) when (IsExpected(verification)) { }
            return new(UpdateExecutionStatus.RecoveryRequired, operation);
        }
    }
    private sealed class UpdatePreparationScope(string capsule) : IDisposable
    {
        private UpdatePreparation? files;
        internal UpdatePreparation Files => files ?? throw new InvalidOperationException("Preparation has not started.");
        internal void Create() { InstallFileSystem.CreatePrivateDirectory(capsule); files = new(capsule); }
        public void Dispose() => files?.Dispose();
    }
    internal static bool IsContained()
    {
        // An internal argv spelling alone cannot turn a normal direct invocation into an unsupervised transaction.
        return QueryInformationJobObject(0, 9, out var limits, (uint)Marshal.SizeOf<JobLimits>(), out _) && (limits.Basic.Flags & 0x2000) != 0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        internal long ProcessTime, JobTime; internal uint Flags; internal nuint MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcesses; internal nuint Affinity; internal uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits
    {
        internal BasicLimits Basic; internal IoCounters Io; internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(nint job, int informationClass, out JobLimits limits, uint length, out uint returned);
    private sealed class PublisherValidationException : IOException { internal PublisherValidationException() : base("The pinned publisher could not be verified.") { } }
    private static WindowsPublisherTrust VerifyPublisher(string path, PublisherPin pin)
    {
        try { return WindowsPublisherTrust.Verify(path, pin); }
        catch (Exception exception) when (IsExpected(exception)) { throw new PublisherValidationException(); }
    }
    private static void AssertIdentity(InstallPayloadManifest manifest, string name, WindowsPublisherTrust identity)
    {
        var entry = manifest.Files.SingleOrDefault(file => file.Path == name);
        if (entry is null || entry.Size != identity.Size || entry.Sha256 != identity.Sha256) throw new InvalidDataException("The signed executable identity does not match its manifest.");
    }
    private static bool IsExpected(Exception exception) => exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or CryptographicException or ArgumentException or InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception;
}
