using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CodeRim.Core.Services;

internal enum InstallCheckpoint { StageVerified, Prepared, OldRenamed, NewRenamed, Committed }
internal enum InstallRecoveryResult { None, RolledBack, Completed }

// Internal byte-only primitive, not an authorization API. U3 must authenticate the publisher/manifest before any production caller is added.
// This engine never executes payloads or reads settings, accounts, PATH, registry, or external CLI profiles.
internal static class InstallTransaction
{
    private const int MaximumJournalBytes = InstallPayloadManifest.MaximumManifestBytes * 4 + 4096;
    private sealed record Journal(string Id, string State, string NewManifest, string? OldManifest);
    internal static void Apply(string installRoot, string archivePath, InstallPayloadManifest next, InstallPayloadManifest? previous = null,
        Action<InstallCheckpoint>? checkpoint = null, CancellationToken token = default)
        => ApplyCore(installRoot, archivePath, next, previous, checkpoint, bindJournal: false, token);
    internal static void ApplyAuthenticated(string installRoot, string archivePath, InstallPayloadManifest next, InstallPayloadManifest? previous, CancellationToken token = default)
        => ApplyCore(installRoot, archivePath, next, previous, null, bindJournal: true, token);
    private static void ApplyCore(string installRoot, string archivePath, InstallPayloadManifest next, InstallPayloadManifest? previous,
        Action<InstallCheckpoint>? checkpoint, bool bindJournal, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(next); ArgumentNullException.ThrowIfNull(archivePath); token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(archivePath)) throw new ArgumentException("An absolute payload archive path is required.", nameof(archivePath));
        var root = NormalizeRoot(installRoot); var work = OpenWork(root); using var lease = Lock(work);
        RecoverLocked(root, work, token, next, previous, bindJournal);
        token.ThrowIfCancellationRequested(); InstallFileSystem.CheckPath(root);
        if (Directory.Exists(root))
        {
            if (previous is null) throw new InvalidDataException("An unmanaged installation cannot be replaced.");
            InstallFileSystem.AssertPrivateDirectory(root); previous.VerifyTree(root, token);
            if (next.Architecture != previous.Architecture || next.Version <= previous.Version) throw new InvalidDataException("An update must preserve architecture and increase its version.");
        }
        else if (previous is not null || File.Exists(root)) throw new InvalidDataException("The expected installed payload is missing.");
        var id = Guid.NewGuid().ToString("N"); var operation = Path.Combine(work, "operation-" + id);
        var stage = Path.Combine(operation, "stage"); var backup = Path.Combine(operation, "backup");
        try
        {
            var journal = new Journal(id, "Extracting", next.ToJson(), previous?.ToJson()); WriteJournal(work, journal);
            InstallFileSystem.CreatePrivateDirectory(operation); InstallFileSystem.CreatePrivateDirectory(stage);
            next.Extract(archivePath, stage, token); checkpoint?.Invoke(InstallCheckpoint.StageVerified);
            next.VerifyTree(stage, token);
            if (previous is not null) previous.VerifyTree(root, token);
            journal = journal with { State = "Prepared" }; WriteJournal(work, journal);
            checkpoint?.Invoke(InstallCheckpoint.Prepared); token.ThrowIfCancellationRequested();
            if (previous is not null)
            {
                InstallFileSystem.CheckPath(root); InstallFileSystem.CheckPath(backup); Directory.Move(root, backup);
                checkpoint?.Invoke(InstallCheckpoint.OldRenamed); WriteJournal(work, journal with { State = "OldMoved" });
            }
            token.ThrowIfCancellationRequested(); InstallFileSystem.CheckPath(stage); InstallFileSystem.CheckPath(root); Directory.Move(stage, root);
            checkpoint?.Invoke(InstallCheckpoint.NewRenamed); WriteJournal(work, journal with { State = "NewMoved" });
            next.VerifyTree(root, token); token.ThrowIfCancellationRequested();
            WriteJournal(work, journal with { State = "Committed" }); checkpoint?.Invoke(InstallCheckpoint.Committed);
            RecoverLocked(root, work, CancellationToken.None, next, previous, bindJournal); // After commit, only validated old binaries are cleaned up.
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            try
            {
                if (File.Exists(JournalPath(work))) RecoverLocked(root, work, CancellationToken.None, next, previous, bindJournal);
                else { next.RemoveOwnedTree(stage, complete: false, CancellationToken.None); RemoveEmptyOperation(operation); }
            }
            catch (Exception recovery) when (recovery is not OutOfMemoryException)
            { throw new AggregateException("The binary update failed; preserved transaction files need recovery.", failure, recovery); }
            throw;
        }
    }
    internal static InstallRecoveryResult Recover(string installRoot, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var root = NormalizeRoot(installRoot); var work = OpenWork(root); using var lease = Lock(work);
        return RecoverLocked(root, work, token);
    }
    internal static InstallRecoveryResult RecoverAuthenticated(string installRoot, InstallPayloadManifest next, InstallPayloadManifest? previous, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var root = NormalizeRoot(installRoot); var work = OpenWork(root); using var lease = Lock(work);
        return RecoverLocked(root, work, token, next, previous, bindJournal: true);
    }
    private static InstallRecoveryResult RecoverLocked(string root, string work, CancellationToken token,
        InstallPayloadManifest? expectedNext = null, InstallPayloadManifest? expectedPrevious = null, bool bindJournal = false)
    {
        var path = JournalPath(work); InstallFileSystem.CheckPath(path);
        if (!File.Exists(path)) return InstallRecoveryResult.None;
        using var document = JsonDocument.Parse(InstallFileSystem.ReadBounded(path, MaximumJournalBytes));
        InstallPayloadManifest.ExactObject(document.RootElement, "Id", "State", "NewManifest", "OldManifest");
        var journal = document.RootElement.Deserialize<Journal>() ?? throw new InvalidDataException("The update journal is invalid.");
        if (!Guid.TryParseExact(journal.Id, "N", out _) || journal.State is not ("Extracting" or "Prepared" or "OldMoved" or "NewMoved" or "Committed" or "Cleaning") || journal.NewManifest is null)
            throw new InvalidDataException("The update journal identity is invalid.");
        var next = InstallPayloadManifest.Parse(journal.NewManifest);
        var previous = journal.OldManifest is null ? null : InstallPayloadManifest.Parse(journal.OldManifest);
        if (previous is not null && (next.Version <= previous.Version || next.Architecture != previous.Architecture)) throw new InvalidDataException("The update journal versions are invalid.");
        if (bindJournal && (next.ToJson() != expectedNext?.ToJson() || previous?.ToJson() != expectedPrevious?.ToJson()))
            throw new InvalidDataException("The journal is not bound to the authenticated update manifests.");
        var operation = Path.Combine(work, "operation-" + journal.Id); var stage = Path.Combine(operation, "stage"); var backup = Path.Combine(operation, "backup");
        InstallFileSystem.CheckPath(root); InstallFileSystem.CheckPath(operation); InstallFileSystem.CheckPath(stage); InstallFileSystem.CheckPath(backup);
        if (journal.State == "Extracting")
        {
            if (Path.Exists(backup)) throw new InvalidDataException("Unexpected backup files must be preserved.");
            if (previous is not null) previous.VerifyTree(root, token);
            else if (Path.Exists(root)) throw new InvalidDataException("Unexpected installed files must be preserved.");
            next.RemoveOwnedTree(stage, complete: false, token); RemoveEmptyOperation(operation); File.Delete(path);
            return InstallRecoveryResult.RolledBack;
        }
        if (journal.State is "Committed" or "Cleaning")
        {
            next.VerifyTree(root, token);
            if (Directory.Exists(stage)) throw new InvalidDataException("Committed staging files are ambiguous and must be preserved.");
            if (Directory.Exists(backup))
            {
                if (previous is null) throw new InvalidDataException("An unknown backup must be preserved.");
                previous.VerifyTree(backup, token, allowMissing: journal.State == "Cleaning");
                if (journal.State == "Committed") WriteJournal(work, journal with { State = "Cleaning" });
                previous.RemoveOwnedTree(backup, complete: false, token);
            }
            RemoveEmptyOperation(operation); File.Delete(path); return InstallRecoveryResult.Completed;
        }
        if (previous is not null)
        {
            if (Directory.Exists(backup))
            {
                previous.VerifyTree(backup, token);
                if (Directory.Exists(root)) MoveNewBackToStage(root, stage, next, token);
                InstallFileSystem.CheckPath(root); InstallFileSystem.CheckPath(backup); Directory.Move(backup, root);
                previous.VerifyTree(root, token);
            }
            else previous.VerifyTree(root, token); // Old rename never happened, or an earlier recovery already restored it.
        }
        else
        {
            if (Directory.Exists(backup)) throw new InvalidDataException("An unknown backup must be preserved.");
            if (Directory.Exists(root)) MoveNewBackToStage(root, stage, next, token);
        }
        if (Directory.Exists(stage)) next.VerifyTree(stage, token, allowMissing: true);
        next.RemoveOwnedTree(stage, complete: false, token);
        RemoveEmptyOperation(operation); File.Delete(path); return InstallRecoveryResult.RolledBack;
    }
    private static void MoveNewBackToStage(string root, string stage, InstallPayloadManifest next, CancellationToken token)
    {
        if (Path.Exists(stage)) throw new InvalidDataException("Multiple new payloads are ambiguous and must be preserved.");
        next.VerifyTree(root, token); InstallFileSystem.CheckPath(root); InstallFileSystem.CheckPath(stage); Directory.Move(root, stage);
    }
    private static void RemoveEmptyOperation(string operation)
    {
        InstallFileSystem.CheckPath(operation);
        if (Directory.Exists(operation)) { InstallFileSystem.CheckStreams(operation); Directory.Delete(operation); } // Never recursively delete unknown files.
    }
    private static string NormalizeRoot(string installRoot)
    {
        ArgumentNullException.ThrowIfNull(installRoot);
        if (!Path.IsPathFullyQualified(installRoot)) throw new ArgumentException("An absolute installation root is required.", nameof(installRoot));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        if (root == Path.GetPathRoot(root) || !Directory.Exists(Path.GetDirectoryName(root))) throw new InvalidDataException("The installation parent must already exist.");
        InstallFileSystem.CheckPath(root);
        InstallPayloadManifest.ValidatePath(Path.GetFileName(root));
        if (OperatingSystem.IsWindows())
        {
            if (root.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException("Network and device installation paths are not supported.");
            root = Directory.Exists(root) ? InstallFileSystem.LongWindowsPath(root)
                : Path.Combine(InstallFileSystem.LongWindowsPath(Path.GetDirectoryName(root)!), Path.GetFileName(root));
            InstallFileSystem.CheckPath(root);
        }
        return root;
    }
    internal static string WorkPath(string root)
    {
        root = NormalizeRoot(root);
        var identity = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(Path.GetDirectoryName(root)!, ".coderim-update-" + hash[..24]);
    }
    private static string OpenWork(string root)
    {
        var work = WorkPath(root); var existed = Directory.Exists(work); InstallFileSystem.CheckPath(work);
        if (!existed) InstallFileSystem.CreatePrivateDirectory(work);
        InstallFileSystem.AssertPrivateDirectory(work);
        var marker = Path.Combine(work, "owner"); var expected = Encoding.UTF8.GetBytes("CodeRim binary transaction v1\n" + root);
        if (existed)
        {
            if (!InstallFileSystem.ReadBounded(marker, 16384).AsSpan().SequenceEqual(expected)) throw new InvalidDataException("The update workspace ownership does not match.");
        }
        else InstallFileSystem.WriteNew(marker, expected);
        return work;
    }
    private static FileStream Lock(string work)
    {
        var path = Path.Combine(work, "commit.lock"); InstallFileSystem.CheckPath(path);
        // Persistent lock file must never be replaced or deleted: all instances use the same inode/handle boundary.
        return InstallFileSystem.OpenLock(path);
    }
    private static string JournalPath(string work) => Path.Combine(work, "journal.json");
    private static void WriteJournal(string work, Journal journal)
    {
        var path = JournalPath(work); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            InstallFileSystem.WriteNew(temporary, JsonSerializer.SerializeToUtf8Bytes(journal));
            InstallFileSystem.CheckPath(path); File.Move(temporary, path, overwrite: true);
        }
        finally { InstallFileSystem.CheckPath(temporary); File.Delete(temporary); }
    }
}

internal static class InstallFileSystem
{
    internal static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked installation locations are not supported."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    internal static void CheckStreams(string path)
    {
        if (OperatingSystem.IsWindows()) CheckWindowsStreams(path);
    }
    [SupportedOSPlatform("windows")]
    private static void CheckWindowsStreams(string path)
    {
        var handle = FindFirstStreamW(path, 0, out var data, 0);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 38 or 87) return; // No streams, or a filesystem without stream support.
            throw new IOException("The installed file streams could not be checked.");
        }
        try
        {
            if (data.Name != "::$DATA" || FindNextStreamW(handle, out _)) throw new InvalidDataException("Unknown installed alternate streams must be preserved.");
            if (Marshal.GetLastPInvokeError() != 38) throw new IOException("The installed stream list is incomplete.");
        }
        finally { FindClose(handle); }
    }
    [SupportedOSPlatform("windows")]
    internal static string LongWindowsPath(string path)
    {
        var capacity = GetLongPathNameW(path, [], 0);
        if (capacity is 0 or > 32768) throw new IOException("The installation path could not be canonicalized.");
        var buffer = new char[capacity]; var length = GetLongPathNameW(path, buffer, capacity);
        if (length == 0 || length >= capacity) throw new IOException("The installation path changed during canonicalization.");
        return new string(buffer, 0, (int)length);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        internal long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] internal string Name;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string fileName, int infoLevel, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextStreamW(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint length);

    internal static void CreatePrivateDirectory(string path)
    {
        CheckPath(path);
        if (Path.Exists(path)) throw new IOException("The transaction directory already exists.");
        if (OperatingSystem.IsWindows()) CreatePrivateWindowsDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        AssertPrivateDirectory(path);
    }
    [SupportedOSPlatform("windows")]
    private static void CreatePrivateWindowsDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User ?? throw new IOException("The current Windows identity is unavailable.");
        var security = new DirectorySecurity(); security.SetOwner(sid); security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }
    internal static void AssertPrivateDirectory(string path)
    {
        CheckPath(path);
        if (OperatingSystem.IsWindows()) AssertPrivateWindowsDirectory(path);
        else if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("The transaction directory is not private to its owner.");
    }
    [SupportedOSPlatform("windows")]
    private static void AssertPrivateWindowsDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User ?? throw new IOException("The current Windows identity is unavailable.");
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!security.AreAccessRulesProtected || !sid.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw new IOException("The transaction directory owner is invalid.");
        var owned = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !sid.Equals(rule.IdentityReference)) throw new IOException("The transaction directory grants access to another principal.");
            if (rule.AccessControlType == AccessControlType.Allow && sid.Equals(rule.IdentityReference) && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl) owned = true;
        }
        if (!owned) throw new IOException("The transaction directory is not owned by the current user.");
    }
    internal static void CreateParents(string root, string parent)
    {
        CheckPath(root); CheckPath(parent);
        if (parent == root) return;
        var relative = Path.GetRelativePath(root, parent); InstallPayloadManifest.ValidatePath(relative.Replace(Path.DirectorySeparatorChar, '/'));
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current)) CreatePrivateDirectory(current);
            else AssertPrivateDirectory(current);
        }
    }
    internal static FileStream CreateFile(string path) => Open(path, FileMode.CreateNew);
    internal static FileStream OpenLock(string path) => Open(path, FileMode.OpenOrCreate);
    private static FileStream Open(string path, FileMode mode)
    {
        CheckPath(path); var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
    internal static void WriteNew(string path, byte[] bytes) { using var file = CreateFile(path); file.Write(bytes); file.Flush(true); }
    internal static byte[] ReadBounded(string path, int maximum)
    {
        CheckPath(path); using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum) throw new InvalidDataException("The transaction metadata is too large.");
        using var result = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (result.Length + read > maximum) throw new InvalidDataException("The transaction metadata grew beyond its limit.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
}
