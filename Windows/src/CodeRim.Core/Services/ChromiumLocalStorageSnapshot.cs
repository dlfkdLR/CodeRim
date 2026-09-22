using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeRim.Core.Services;

public enum ChromiumStorageFailure { InvalidData, UnsupportedFormat, PolicyLimit, BusyOrChanged, Missing }

public sealed class ChromiumStorageException : IOException
{
    public ChromiumStorageFailure Failure { get; }
    public ChromiumStorageException(ChromiumStorageFailure failure) : base(failure switch
    {
        ChromiumStorageFailure.UnsupportedFormat => "The selected browser storage format is not supported.",
        ChromiumStorageFailure.PolicyLimit => "The selected browser storage exceeds the read limits.",
        ChromiumStorageFailure.BusyOrChanged => "The selected browser storage is busy or changed. Close the browser and try again.",
        ChromiumStorageFailure.Missing => "The selected browser storage is missing.",
        _ => "The selected browser storage is incomplete or invalid."
    }) => Failure = failure;
}

/// <summary>Reads only explicitly requested current localStorage values from a quiescent profile.
/// No discovery, cookie decryption, source writes, database engine recovery or historical token scan.
/// ASCII script keys require canonical Latin1 encoding; legacy UTF16 aliases are unsupported,
/// including deleted aliases in active files. Values may use either supported encoding.</summary>
public static class ChromiumLocalStorageSnapshot
{
    private static readonly Encoding StrictUtf16 = new UnicodeEncoding(false, false, true);
    private static readonly byte[] VersionKey = "VERSION"u8.ToArray();
    public static IReadOnlyDictionary<string, string> Read(string directory, string origin, IReadOnlyCollection<string> keys, CancellationToken token = default)
        => Read(directory, origin, keys, null, token);

    internal static IReadOnlyDictionary<string, string> Read(string directory, string origin, IReadOnlyCollection<string> keys, Action? afterCapture, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(keys);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
            || origin != uri.GetLeftPart(UriPartial.Authority) || uri.HostNameType != UriHostNameType.Dns) throw new ArgumentException("Choose an exact HTTPS origin.", nameof(origin));
        if (keys.Count is < 1 or > 64 || keys.Any(key => key is null || key.Length is < 1 or > 256 || key.Any(c => c < 0x20 || c > 0x7e)) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
            throw new ArgumentException("Choose a bounded set of exact localStorage keys.", nameof(keys));
        var parser = new BoundedLevelDbReader(token); var streams = new List<FileStream>();
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (OperatingSystem.IsWindows() && (root.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Network))
                throw new ChromiumStorageException(ChromiumStorageFailure.UnsupportedFormat);
            CheckPath(root, true);
            var initial = Inventory(root); var snapshots = new Dictionary<string, byte[]>(StringComparer.Ordinal); long total = 0;
            byte[] Capture(string name, int maximum)
            {
                parser.Check(); if (!initial.Contains(name)) throw new ChromiumStorageException(ChromiumStorageFailure.Missing);
                var path = Path.Combine(root, name); CheckPath(path, false);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); streams.Add(stream); CheckPath(path, false);
                BoundedLevelDbReader.Limit(stream.Length <= maximum && total + stream.Length <= BoundedLevelDbReader.MaximumBytes);
                var bytes = new byte[(int)stream.Length]; var at = 0;
                while (at < bytes.Length)
                { parser.Check(); var read = stream.Read(bytes, at, Math.Min(16384, bytes.Length - at)); if (read == 0) throw BoundedLevelDbReader.Invalid(); at += read; }
                if (stream.ReadByte() != -1) throw Changed(); parser.Check(); total += bytes.Length; snapshots.Add(name, bytes); return bytes;
            }
            var current = Capture("CURRENT", 128);
            if (current.Length < 11 || current[^1] != 10 || current.AsSpan(0, current.Length - 1).IndexOfAnyExceptInRange((byte)0x20, (byte)0x7e) >= 0) throw BoundedLevelDbReader.Invalid();
            var manifestName = Encoding.ASCII.GetString(current.AsSpan(0, current.Length - 1));
            if (!manifestName.StartsWith("MANIFEST-", StringComparison.Ordinal) || !Number(manifestName[9..], out _)) throw BoundedLevelDbReader.Invalid();
            var version = parser.Manifest(Capture(manifestName, 8 * 1024 * 1024));
            var eligible = Eligible(initial, version); BoundedLevelDbReader.Limit(eligible.Count + version.Tables.Count + 2 <= 128); var tables = new Dictionary<ulong, byte[]>();
            foreach (var table in version.Tables.Keys)
            {
                var names = initial.Where(name => TableNumber(name, out var number) && number == table).ToArray();
                if (names.Length != 1) throw BoundedLevelDbReader.Invalid(); tables.Add(table, Capture(names[0], BoundedLevelDbReader.MaximumFile));
            }
            var logs = new List<byte[]>(); foreach (var name in eligible) logs.Add(Capture(name, BoundedLevelDbReader.MaximumFile));
            BoundedLevelDbReader.Limit(snapshots.Count <= 128); afterCapture?.Invoke(); parser.Check();
            var prefix = Encoding.UTF8.GetBytes("_" + origin + "\0");
            byte[] EncodedKey(string key, bool latin)
            {
                var text = latin ? Encoding.Latin1.GetBytes(key) : StrictUtf16.GetBytes(key); var result = new byte[prefix.Length + 1 + text.Length];
                prefix.CopyTo(result, 0); result[prefix.Length] = latin ? (byte)1 : (byte)0; text.CopyTo(result, prefix.Length + 1); return result;
            }
            var requested = new List<byte[]> { VersionKey }; var noncanonical = new List<byte[]>();
            foreach (var key in keys) { requested.Add(EncodedKey(key, true)); noncanonical.Add(EncodedKey(key, false)); }
            var values = parser.Read(version, tables, logs, requested, noncanonical);
            if (!values.TryGetValue(Convert.ToHexString(VersionKey), out var schema) || !schema.AsSpan().SequenceEqual("1"u8)) throw new ChromiumStorageException(ChromiumStorageFailure.UnsupportedFormat);
            var result = new Dictionary<string, string>(StringComparer.Ordinal); var returned = 0;
            foreach (var key in keys)
            {
                if (!values.TryGetValue(Convert.ToHexString(EncodedKey(key, true)), out var bytes)) continue;
                returned += bytes.Length; BoundedLevelDbReader.Limit(returned <= 65536);
                result[key] = Decode(bytes);
            }
            if (!initial.SetEquals(Inventory(root))) throw Changed();
            foreach (var entry in snapshots)
            {
                parser.Check(); var path = Path.Combine(root, entry.Key); CheckPath(path, false);
                using var verify = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (verify.Length != entry.Value.Length) throw Changed();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[16384]; int read; var verifiedBytes = 0;
                while ((read = verify.Read(buffer)) != 0) { parser.Check(); verifiedBytes += read; if (verifiedBytes > entry.Value.Length) throw Changed(); hash.AppendData(buffer, 0, read); }
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), SHA256.HashData(entry.Value))) throw Changed();
            }
            parser.Check(); return new ReadOnlyDictionary<string, string>(result);
        }
        catch (ChromiumStorageException) { throw; }
        catch (DecoderFallbackException) { throw BoundedLevelDbReader.Invalid(); }
        catch (FileNotFoundException) { throw new ChromiumStorageException(ChromiumStorageFailure.Missing); }
        catch (DirectoryNotFoundException) { throw new ChromiumStorageException(ChromiumStorageFailure.Missing); }
        catch (IOException) { throw Changed(); }
        catch (UnauthorizedAccessException) { throw Changed(); }
        finally { foreach (var stream in streams) stream.Dispose(); }
    }
    private static ChromiumStorageException Changed() => new(ChromiumStorageFailure.BusyOrChanged);
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) throw BoundedLevelDbReader.Invalid();
        return bytes[0] switch
        { 0 when (bytes.Length & 1) == 1 => StrictUtf16.GetString(bytes, 1, bytes.Length - 1), 1 => Encoding.Latin1.GetString(bytes, 1, bytes.Length - 1), _ => throw BoundedLevelDbReader.Invalid() };
    }
    private static void CheckPath(string path, bool directory)
    {
        for (var item = path; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new ChromiumStorageException(ChromiumStorageFailure.UnsupportedFormat);
        if (((File.GetAttributes(path) & FileAttributes.Directory) != 0) != directory) throw BoundedLevelDbReader.Invalid();
    }
    private static HashSet<string> Inventory(string root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal); var count = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        { BoundedLevelDbReader.Limit(++count <= 512); var name = Path.GetFileName(path); if (!names.Add(name)) throw BoundedLevelDbReader.Invalid(); }
        return names;
    }
    private static bool Number(string text, out ulong number)
    { number = 0; return text.Length is >= 1 and <= 20 && text.All(char.IsAsciiDigit) && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0; }
    private static bool TableNumber(string name, out ulong number)
    { number = 0; return (name.EndsWith(".ldb", StringComparison.Ordinal) || name.EndsWith(".sst", StringComparison.Ordinal)) && Number(name[..^4], out number); }
    private static List<string> Eligible(HashSet<string> names, BoundedLevelDbReader.Version version)
    {
        var logs = new SortedDictionary<ulong, string>();
        foreach (var name in names)
            if (name.EndsWith(".log", StringComparison.Ordinal) && Number(name[..^4], out var number) && (number >= version.Log || number == version.PreviousLog))
            { if (!logs.TryAdd(number, name)) throw BoundedLevelDbReader.Invalid(); }
        BoundedLevelDbReader.Limit(logs.Count <= 128);
        // A normal quiescent database keeps a recovery log. Do not guess at a partially copied store.
        if (logs.Count == 0) throw new ChromiumStorageException(ChromiumStorageFailure.Missing);
        return logs.Values.ToList();
    }
}
