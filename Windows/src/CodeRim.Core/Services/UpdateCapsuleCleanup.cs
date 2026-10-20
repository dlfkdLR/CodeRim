using System.Runtime.Versioning;
using System.Text.Json;

namespace CodeRim.Core.Services;

// Ownership of this invocation's private temporary files only. This never authenticates or installs payload bytes.
internal static class UpdateCapsuleCleanup
{
    internal static readonly string[] OwnedNames = ["previous-worker.exe", "previous-app.exe", "next-worker.exe", "next-app.exe", "package.zip", "request.json"];
    private static readonly string[] InitialNames = ["next-worker.exe", "package.zip"];
    internal static byte[] ReceiptBytes(UpdateWorkerRequest request, IEnumerable<InstallPayloadFile> files) => ReceiptBytes(request.Version, request.Architecture, files, initial: false);
    internal static byte[] InitialReceiptBytes(InstallPayloadManifest manifest, IEnumerable<InstallPayloadFile> files) => ReceiptBytes(manifest.Version.ToString(3), manifest.Architecture, files, initial: true);
    private static byte[] ReceiptBytes(string version, string architecture, IEnumerable<InstallPayloadFile> files, bool initial)
    {
        var owned = files.ToArray();
        if (!owned.Select(file => file.Path).Order(StringComparer.Ordinal).SequenceEqual((initial ? InitialNames : OwnedNames).Order(StringComparer.Ordinal)))
            throw new InvalidDataException("The update capsule ownership set is invalid.");
        var manifest = InstallPayloadManifest.Parse(JsonSerializer.Serialize(new
        {
            schema = 1, product = "CodeRim", version, architecture,
            files = owned.Select(file => new { path = file.Path, size = file.Size, sha256 = file.Sha256 })
        }));
        return System.Text.Encoding.UTF8.GetBytes(manifest.ToJson());
    }
    internal static void WriteReceipt(string capsule, UpdateWorkerRequest request, IEnumerable<InstallPayloadFile> files)
    {
        InstallFileSystem.WriteNew(Path.Combine(capsule, InstallPayloadManifest.ReceiptName), ReceiptBytes(request, files));
    }
    internal static bool TryRemoveCompleted(string capsule)
    {
        try
        {
            InstallFileSystem.AssertPrivateDirectory(capsule);
            var receipt = InstallPayloadManifest.Parse(new System.Text.UTF8Encoding(false, true).GetString(InstallFileSystem.ReadBounded(Path.Combine(capsule, InstallPayloadManifest.ReceiptName), InstallPayloadManifest.MaximumManifestBytes)));
            var names = receipt.Files.Select(file => file.Path).Order(StringComparer.Ordinal).ToArray();
            if (!names.SequenceEqual(OwnedNames.Order(StringComparer.Ordinal)) && !names.SequenceEqual(InitialNames.Order(StringComparer.Ordinal))) return false;
            receipt.VerifyTree(capsule, CancellationToken.None, allowMissing: true);
            // The ownership receipt is removed last so an interrupted cleanup can validate remaining files again.
            foreach (var name in names)
            {
                var path = Path.Combine(capsule, name); InstallFileSystem.CheckPath(path); if (File.Exists(path)) File.Delete(path);
            }
            File.Delete(Path.Combine(capsule, InstallPayloadManifest.ReceiptName)); Directory.Delete(capsule); return true;
        }
        catch (Exception exception) when (UpdateBootstrap.Expected(exception)) { return false; }
    }
    [SupportedOSPlatform("windows")]
    internal static UpdateExecutionResult AbandonPrepared(WindowsUpdateLocation location, UpdateExecutionResult result)
    {
        // Only a fully prepared, never-committed operation is passed here. This does not claim an installation was rolled back.
        var capsule = location.Capsule(result.OperationId!);
        if (Directory.Exists(capsule)) _ = TryRemoveCompleted(capsule);
        UpdateBootstrap.WriteResult(Path.Combine(location.PrivateDirectory("Results"), result.OperationId + "-" + Guid.NewGuid().ToString("N") + ".json"), result);
        return result;
    }
    [SupportedOSPlatform("windows")]
    internal static void CollectCompleted(WindowsUpdateLocation location)
    {
        foreach (var directory in Directory.EnumerateDirectories(location.PrivateDirectory("Completed")).Take(8))
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) _ = TryRemoveCompleted(directory);
    }
    [SupportedOSPlatform("windows")]
    internal static UpdateExecutionResult Record(WindowsUpdateLocation location, UpdateExecutionResult result)
    {
        if (result.Status is UpdateExecutionStatus.Applied or UpdateExecutionStatus.RolledBack)
        {
            try
            {
                var original = location.Capsule(result.OperationId!); var completed = Path.Combine(location.PrivateDirectory("Completed"), result.OperationId!);
                InstallFileSystem.AssertPrivateDirectory(original); InstallFileSystem.CheckPath(completed);
                Directory.Move(original, completed); // Remove the completed operation from the active recovery namespace before deleting temporary bytes.
                if (!TryRemoveCompleted(completed) && result.Status == UpdateExecutionStatus.Applied) result = result with { Status = UpdateExecutionStatus.AppliedCleanupPending };
            }
            catch (Exception exception) when (UpdateBootstrap.Expected(exception)) { if (result.Status == UpdateExecutionStatus.Applied) result = result with { Status = UpdateExecutionStatus.AppliedCleanupPending }; }
        }
        UpdateBootstrap.WriteResult(Path.Combine(location.PrivateDirectory("Results"), result.OperationId + "-" + Guid.NewGuid().ToString("N") + ".json"), result);
        return result;
    }
}
