using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeRim.Core.Services;

// Byte integrity/ownership only. Kept internal until a publisher-authenticated U3 entry point is available.
internal sealed record InstallPayloadFile(string Path, long Size, string Sha256);
internal sealed class InstallPayloadManifest
{
    internal const int MaximumManifestBytes = 1_048_576;
    internal const int MaximumFiles = 4096;
    internal const long MaximumFileBytes = 512L * 1024 * 1024;
    internal const long MaximumTotalBytes = 1024L * 1024 * 1024;
    internal const string ReceiptName = ".coderim-install.json";
    private static readonly SearchValues<char> Hex = SearchValues.Create("0123456789abcdefABCDEF");
    private readonly Dictionary<string, InstallPayloadFile> byPath;
    private readonly HashSet<string> directories;
    internal Version Version { get; }
    internal string Architecture { get; }
    internal ReadOnlyCollection<InstallPayloadFile> Files { get; }
    private InstallPayloadManifest(Version version, string architecture, List<InstallPayloadFile> files)
    {
        Version = version; Architecture = architecture;
        Files = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList().AsReadOnly();
        byPath = Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        directories = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            var path = file.Path;
            while (path.LastIndexOf('/') is var slash && slash >= 0) { path = path[..slash]; directories.Add(path); }
        }
        if (directories.Count > MaximumFiles) throw new InvalidDataException("The payload has too many directories.");
        if (directories.Any(byPath.ContainsKey)) throw new InvalidDataException("A payload path is both a file and directory.");
    }
    internal static InstallPayloadManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes) throw new InvalidDataException("The payload manifest is too large.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); var root = document.RootElement;
        ExactObject(root, "schema", "product", "version", "architecture", "files");
        if (root.GetProperty("schema").ValueKind != JsonValueKind.Number || !root.GetProperty("schema").TryGetInt32(out var schema) || schema != 1 || Text(root, "product") != "CodeRim")
            throw new InvalidDataException("The payload identity is invalid.");
        var text = Text(root, "version");
        if (text is null || !System.Version.TryParse(text, out var version) || version.Build < 0 || version.Revision >= 0 || text != version.ToString(3))
            throw new InvalidDataException("The payload version is invalid.");
        var architecture = Text(root, "architecture");
        if (architecture is not ("x64" or "arm64")) throw new InvalidDataException("The payload architecture is invalid.");
        var values = root.GetProperty("files");
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > MaximumFiles) throw new InvalidDataException("The payload file count is invalid.");
        var files = new List<InstallPayloadFile>(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var value in values.EnumerateArray())
        {
            ExactObject(value, "path", "size", "sha256");
            var path = Text(value, "path") ?? throw new InvalidDataException("The payload path is invalid."); ValidatePath(path);
            if (!paths.Add(path)) throw new InvalidDataException("The payload path is ambiguous.");
            if (value.GetProperty("size").ValueKind != JsonValueKind.Number || !value.GetProperty("size").TryGetInt64(out var size) || size < 0 || size > MaximumFileBytes)
                throw new InvalidDataException("The payload size is invalid.");
            total += size; if (total > MaximumTotalBytes) throw new InvalidDataException("The payload is too large.");
            var hash = Text(value, "sha256");
            if (hash is not { Length: 64 } || hash.AsSpan().ContainsAnyExcept(Hex)) throw new InvalidDataException("The payload hash is invalid.");
            files.Add(new(path, size, hash.ToLowerInvariant()));
        }
        var result = new InstallPayloadManifest(version, architecture, files);
        if (Encoding.UTF8.GetByteCount(result.ToJson()) > MaximumManifestBytes) throw new InvalidDataException("The canonical payload manifest is too large.");
        return result;
    }
    internal string ToJson() => JsonSerializer.Serialize(new
    {
        schema = 1, product = "CodeRim", version = Version.ToString(3), architecture = Architecture,
        files = Files.Select(file => new { path = file.Path, size = file.Size, sha256 = file.Sha256 })
    });
    internal static void ValidatePath(string path)
    {
        if (path.Length is < 1 or > 240 || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.EndsWith('/'))
            throw new InvalidDataException("The payload path is not relative and portable.");
        var parts = path.Split('/');
        if (parts.Length > 16) throw new InvalidDataException("The payload path is too deep.");
        foreach (var part in parts)
        {
            if (part.Length is < 1 or > 120 || part is "." or ".." || part != part.Trim() || part.EndsWith('.') || part.Equals(ReceiptName, StringComparison.OrdinalIgnoreCase)
                || part.Any(character => character < 32 || character == 127 || "<>\"|?*".Contains(character)))
                throw new InvalidDataException("The payload path contains an unsupported component.");
            var name = part.Split('.')[0].ToUpperInvariant();
            if (name is "CON" or "PRN" or "AUX" or "NUL" || name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(name[3]))
                throw new InvalidDataException("The payload path is a reserved device name.");
        }
    }
    internal static void ExactObject(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The payload metadata must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !names.Contains(property.Name, StringComparer.Ordinal)) throw new InvalidDataException("The payload metadata is ambiguous.");
        if (seen.Count != names.Length) throw new InvalidDataException("The payload metadata is incomplete.");
    }
    private static string? Text(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.String ? value.GetProperty(name).GetString() : null;

    internal void Extract(string archivePath, string stage, CancellationToken token)
    {
        InstallFileSystem.CheckPath(archivePath); InstallFileSystem.CheckPath(stage);
        if (Directory.EnumerateFileSystemEntries(stage).Any()) throw new IOException("The payload staging directory is not empty.");
        using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > ReleasePackageDownload.MaximumPackageBytes) throw new InvalidDataException("The payload archive is too large.");
        CheckCentralDirectory(input); input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/'); var relative = directory ? entry.FullName[..^1] : entry.FullName; ValidatePath(relative);
            if (!seen.Add(relative)) throw new InvalidDataException("The archive contains duplicate paths.");
            var kind = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || kind != 0 && kind != (directory ? 0x4000 : 0x8000))
                throw new InvalidDataException("Linked or special archive entries are not supported.");
            if (directory)
            {
                if (!directories.Contains(relative) || entry.Length != 0) throw new InvalidDataException("The archive contains an unknown directory.");
                continue;
            }
            if (!byPath.TryGetValue(relative, out var file) || file.Path != relative || entry.Length != file.Size)
                throw new InvalidDataException("The archive does not match its file manifest.");
            if (entry.Length > Math.Max(1, entry.CompressedLength) * 200) throw new InvalidDataException("The archive compression ratio is excessive.");
            var destination = FullPath(stage, relative); InstallFileSystem.CreateParents(stage, Path.GetDirectoryName(destination)!);
            using var source = entry.Open();
            using var output = InstallFileSystem.CreateFile(destination);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long total = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested(); var count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, file.Size - total + 1));
                if (count == 0) break; total += count;
                if (total > file.Size) throw new InvalidDataException("The expanded payload is too large.");
                digest.AppendData(buffer, 0, count); output.Write(buffer, 0, count);
            }
            if (total != file.Size || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(file.Sha256)))
                throw new InvalidDataException("The expanded payload does not match its hash.");
            output.Flush(true); present.Add(relative);
        }
        if (present.Count != Files.Count) throw new InvalidDataException("The archive is missing payload files.");
        InstallFileSystem.WriteNew(Path.Combine(stage, ReceiptName), Encoding.UTF8.GetBytes(ToJson()));
        VerifyTree(stage, token);
    }
    // Reject enormous/forged central directories before ZipArchive allocates one object per entry. ZIP64/multidisk/encrypted payloads are not supported.
    internal static void CheckCentralDirectory(FileStream input)
    {
        if (input.Length < 22) throw new InvalidDataException("The archive directory is missing.");
        var tail = new byte[(int)Math.Min(input.Length, 65557)]; input.Position = input.Length - tail.Length; input.ReadExactly(tail);
        var end = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) == 0x06054b50 && index + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20)) == tail.Length) { end = index; break; }
        if (end < 0) throw new InvalidDataException("The archive end record is invalid.");
        var record = tail.AsSpan(end); var count = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]); var offset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(record[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(record[8..]) != count
            || count is < 1 or > MaximumFiles * 2 || size > 4 * 1024 * 1024 || (long)offset + size != input.Length - tail.Length + end)
            throw new InvalidDataException("The archive directory bounds are invalid.");
        input.Position = offset; var limit = (long)offset + size; var actual = 0; var header = new byte[46];
        while (input.Position < limit)
        {
            if (++actual > MaximumFiles * 2 || limit - input.Position < header.Length) throw new InvalidDataException("The archive directory count is invalid.");
            input.ReadExactly(header); var span = header.AsSpan();
            if (BinaryPrimitives.ReadUInt32LittleEndian(span) != 0x02014b50 || (BinaryPrimitives.ReadUInt16LittleEndian(span[8..]) & 0x41) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(span[20..]) == uint.MaxValue || BinaryPrimitives.ReadUInt32LittleEndian(span[24..]) == uint.MaxValue
                || BinaryPrimitives.ReadUInt16LittleEndian(span[34..]) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(span[42..]) >= offset)
                throw new InvalidDataException("The archive directory entry is unsupported.");
            var extra = BinaryPrimitives.ReadUInt16LittleEndian(span[28..]) + BinaryPrimitives.ReadUInt16LittleEndian(span[30..]) + BinaryPrimitives.ReadUInt16LittleEndian(span[32..]);
            if (input.Position + extra > limit) throw new InvalidDataException("The archive directory entry exceeds its bounds.");
            input.Position += extra;
        }
        if (actual != count || input.Position != limit) throw new InvalidDataException("The archive directory count does not match.");
    }
    internal static string FullPath(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    internal void VerifyTree(string root, CancellationToken token, bool allowMissing = false)
    {
        var paths = EnumerateOwned(root);
        if (!allowMissing && (paths.Files.Count != Files.Count + 1 || !paths.Files.Contains(ReceiptName, StringComparer.Ordinal))) throw new InvalidDataException("The installed payload is incomplete.");
        if (paths.Files.Contains(ReceiptName, StringComparer.Ordinal))
        {
            var receipt = InstallFileSystem.ReadBounded(Path.Combine(root, ReceiptName), MaximumManifestBytes);
            if (!receipt.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(ToJson()))) throw new InvalidDataException("The installed payload receipt does not match.");
        }
        foreach (var file in Files)
        {
            if (allowMissing && !paths.Files.Contains(file.Path, StringComparer.Ordinal)) continue;
            token.ThrowIfCancellationRequested(); var path = FullPath(root, file.Path); InstallFileSystem.CheckPath(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != file.Size) throw new InvalidDataException("The installed payload size does not match.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long total = 0; int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested(); total += count;
                if (total > file.Size) throw new InvalidDataException("The installed payload grew during verification.");
                hash.AppendData(buffer, 0, count);
            }
            if (total != file.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(file.Sha256))) throw new InvalidDataException("The installed payload hash does not match.");
        }
    }
    internal void VerifyUninstalledTree(string root, CancellationToken token)
    {
        var paths = EnumerateOwned(root);
        if (paths.Files.Count != Files.Count || paths.Files.Contains(ReceiptName, StringComparer.Ordinal))
            throw new InvalidDataException("The extracted signed payload is incomplete or contains an installed receipt.");
        VerifyTree(root, token, allowMissing: true); // Enumeration above requires every exact known file, with no receipt.
    }
    private (List<string> Files, List<string> Directories) EnumerateOwned(string root)
    {
        InstallFileSystem.CheckPath(root); InstallFileSystem.CheckStreams(root); var files = new List<string>(); var folders = new List<string>();
        var pending = new Stack<string>(); pending.Push(""); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count != 0)
        {
            var prefix = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(FullPath(root, prefix)))
            {
                InstallFileSystem.CheckPath(path); InstallFileSystem.CheckStreams(path); var relative = prefix.Length == 0 ? Path.GetFileName(path) : prefix + "/" + Path.GetFileName(path);
                if (!seen.Add(relative) || seen.Count > MaximumFiles * 2 + 1) throw new InvalidDataException("The installed paths are ambiguous.");
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                {
                    if (!directories.Contains(relative)) throw new InvalidDataException("An unknown installed directory must be preserved.");
                    folders.Add(relative); pending.Push(relative);
                }
                else
                {
                    if (relative != ReceiptName && (!byPath.TryGetValue(relative, out var file) || relative != file.Path)) throw new InvalidDataException("An unknown installed file must be preserved.");
                    files.Add(relative);
                }
            }
        }
        return (files, folders);
    }
    internal void RemoveOwnedTree(string root, bool complete, CancellationToken token)
    {
        if (!Directory.Exists(root)) return;
        if (complete) VerifyTree(root, token);
        var paths = EnumerateOwned(root); // Finish all ownership checks before deleting anything.
        foreach (var file in paths.Files) { InstallFileSystem.CheckPath(FullPath(root, file)); File.Delete(FullPath(root, file)); }
        foreach (var directory in paths.Directories.OrderByDescending(path => path.Length)) { InstallFileSystem.CheckPath(FullPath(root, directory)); Directory.Delete(FullPath(root, directory)); }
        InstallFileSystem.CheckPath(root); Directory.Delete(root);
    }
}
