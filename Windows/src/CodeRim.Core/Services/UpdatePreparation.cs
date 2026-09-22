using System.Security.Cryptography;

namespace CodeRim.Core.Services;

// Tracks only files successfully CreateNew'd by this invocation. No unknown/replaced file is eligible for pre-commit cleanup.
internal sealed class UpdatePreparation : IDisposable
{
    private sealed record Owned(string Path, long Size, string Hash, UsageScanner.FileIdentity? Identity);
    private readonly string root;
    private readonly List<Owned> owned = [];
    private bool retained;
    private bool incomplete;
    internal UpdatePreparation(string root) { this.root = root; InstallFileSystem.AssertPrivateDirectory(root); }
    internal void KeepForRecovery() => retained = true;
    internal void Write(string name, Action<FileStream> write)
    {
        if (!UpdateCapsuleCleanup.OwnedNames.Contains(name, StringComparer.Ordinal) && name != InstallPayloadManifest.ReceiptName)
            throw new InvalidDataException("Unexpected preparation file.");
        var path = Path.Combine(root, name);
        using var output = InstallFileSystem.CreateFile(path);
        try { write(output); output.Flush(true); }
        finally
        {
            try
            {
                if (output.Length > InstallPayloadManifest.MaximumFileBytes) incomplete = true;
                else
                {
                    output.Position = 0;
                    owned.Add(new(path, output.Length, Convert.ToHexStringLower(SHA256.HashData(output)), UsageScanner.FileIdentity.TryRead(output.SafeFileHandle)));
                }
            }
            catch (Exception exception) when (UpdateBootstrap.Expected(exception)) { incomplete = true; }
        }
    }
    internal void Copy(string source, string name, long size, string hash) => Write(name, output =>
    {
        InstallFileSystem.CheckPath(source); InstallFileSystem.CheckStreams(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        SignedInstallPayload.CopyAndHash(input, output, size, hash);
    });
    internal void DisposeUncommitted()
    {
        if (retained || incomplete) return;
        try
        {
            var paths = Directory.EnumerateFileSystemEntries(root).Take(owned.Count + 1).ToArray();
            if (paths.Length != owned.Count || paths.Any(path => !owned.Any(file => file.Path == path))) return;
            foreach (var file in owned)
            {
                InstallFileSystem.CheckPath(file.Path); InstallFileSystem.CheckStreams(file.Path);
                using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length != file.Size || UsageScanner.FileIdentity.TryRead(input.SafeFileHandle) != file.Identity
                    || Convert.ToHexStringLower(SHA256.HashData(input)) != file.Hash) return;
            }
            foreach (var file in owned) { InstallFileSystem.CheckPath(file.Path); File.Delete(file.Path); }
            Directory.Delete(root);
        }
        catch (Exception exception) when (UpdateBootstrap.Expected(exception)) { }
    }
    public void Dispose() => DisposeUncommitted();
}
