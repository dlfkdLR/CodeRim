using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace CodeRim.Core.Services;

/// <summary>Recovers only a complete MiMo session from one explicitly selected Firefox profile.</summary>
public static class MiMoFirefoxSessionImport
{
    public const int MaximumInputBytes = 64 * 1024 * 1024;
    public const int MaximumOutputBytes = 128 * 1024 * 1024;
    private static readonly string[] Domains = ["xiaomimimo.com"];
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
        { "api-platform_serviceToken", "userId", "api-platform_ph", "api-platform_slh" };
    private static readonly Uri Target = new("https://platform.xiaomimimo.com/api/v1/balance");
    private sealed class LimitException : IOException { }
    private sealed class UnstableException : IOException { }

    public static BrowserCookieJar Read(BrowserProfile profile, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var persisted = FirefoxCookieImport.Read(profile, Domains, now, cancellationToken);
        var started = Stopwatch.StartNew();
        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (started.Elapsed > TimeSpan.FromSeconds(3)) throw new LimitException();
        }
        try
        {
            foreach (var path in Candidates(profile.Directory))
            {
                CheckBudget();
                if (!File.Exists(path)) continue;
                try
                {
                    var content = ReadStable(path, CheckBudget);
                    var decoded = Decode(content, CheckBudget);
                    using var document = JsonDocument.Parse(decoded, new JsonDocumentOptions { MaxDepth = 32 });
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                    // A valid current state is authoritative, including an empty or
                    // partial cookie list. Never resurrect cookies from older backups.
                    var session = Cookies(document.RootElement, now, CheckBudget);
                    var header = session is null ? null : SessionHeader(session, now);
                    return Complete(header) ? session! : persisted;
                }
                catch (Exception error) when (error is LimitException or UnstableException) { return persisted; }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
                { /* malformed/unreadable candidate: next fixed backup */ }
            }
        }
        catch (LimitException) { return persisted; }
        return persisted;
    }
    private static IEnumerable<string> Candidates(string root)
    {
        yield return Path.Combine(root, "sessionstore.jsonlz4");
        var backups = Path.Combine(root, "sessionstore-backups");
        yield return Path.Combine(backups, "recovery.jsonlz4");
        yield return Path.Combine(backups, "recovery.baklz4");
        yield return Path.Combine(backups, "previous.jsonlz4");
        if (!Directory.Exists(backups)) yield break;
        RejectLinks(backups);
        var upgrades = Directory.EnumerateFiles(backups, "upgrade.jsonlz4-*", SearchOption.TopDirectoryOnly).Take(257).ToArray();
        if (upgrades.Length > 256) throw new LimitException();
        var latest = upgrades.OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault();
        if (latest is not null) yield return latest;
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked browser session files are not supported.");
    }
    private static byte[] ReadStable(string path, Action check)
    {
        try { RejectLinks(path); }
        catch (InvalidDataException) { throw new UnstableException(); }
        var before = new FileInfo(path);
        var stamp = (before.Length, before.LastWriteTimeUtc, before.CreationTimeUtc);
        if (before.Length > MaximumInputBytes) throw new LimitException();
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var identity = UsageScanner.FileIdentity.TryRead(input.SafeFileHandle);
        if (OperatingSystem.IsWindows() && identity is null) throw new InvalidDataException("The browser session could not be identified.");
        if (input.Length != stamp.Length || input.Length > MaximumInputBytes) throw new LimitException();
        var bytes = new byte[(int)input.Length]; var offset = 0;
        while (offset < bytes.Length)
        {
            check(); var read = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
            if (read == 0) throw new UnstableException();
            offset += read;
        }
        check();
        try
        {
            RejectLinks(path);
            var after = new FileInfo(path);
            if (input.ReadByte() != -1 || stamp != (after.Length, after.LastWriteTimeUtc, after.CreationTimeUtc)
                || identity != UsageScanner.FileIdentity.TryRead(path))
                throw new UnstableException();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { throw new UnstableException(); }
        return bytes;
    }
    private static byte[] Decode(byte[] source, Action check)
    {
        if (source.Length < 12 || !source.AsSpan(0, 8).SequenceEqual("mozLz40\0"u8)) throw new InvalidDataException();
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(8, 4));
        if (declared > MaximumOutputBytes) throw new LimitException();
        var output = new byte[(int)declared]; var input = 12; var count = 0;
        int Length(int initial)
        {
            var length = initial;
            if (initial != 15) return length;
            int next;
            do
            {
                check();
                if (input >= source.Length) throw new InvalidDataException();
                next = source[input++]; length += next;
                if (length > MaximumOutputBytes) throw new LimitException();
            } while (next == 255);
            return length;
        }
        while (input < source.Length)
        {
            check(); var marker = source[input++]; var literal = Length(marker >> 4);
            if (literal > source.Length - input || literal > output.Length - count) throw new InvalidDataException();
            source.AsSpan(input, literal).CopyTo(output.AsSpan(count)); input += literal; count += literal;
            if (input == source.Length) break;
            if (source.Length - input < 2) throw new InvalidDataException();
            var distance = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(input, 2)); input += 2;
            if (distance == 0 || distance > count) throw new InvalidDataException();
            var match = Length(marker & 15) + 4;
            if (match > output.Length - count) throw new InvalidDataException();
            for (var i = 0; i < match; i++)
            {
                if ((i & 32767) == 0) check();
                output[count] = output[count - distance]; count++;
            }
        }
        if (count != output.Length) throw new InvalidDataException();
        return output;
    }
    private static bool Unique(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(field => names.Add(field.Name));
    }
    private static bool IsDefaultContext(JsonElement cookie)
    {
        if (cookie.TryGetProperty("isPartitioned", out var partitioned) && partitioned.ValueKind != JsonValueKind.False) return false;
        if (!cookie.TryGetProperty("originAttributes", out var attributes)) return true;
        if (attributes.ValueKind == JsonValueKind.String) return attributes.GetString() == "";
        if (attributes.ValueKind != JsonValueKind.Object || !Unique(attributes)) return false;
        foreach (var field in attributes.EnumerateObject())
        {
            if (field.Name is "userContextId" or "privateBrowsingId")
            {
                if (field.Value.ValueKind != JsonValueKind.Number || field.Value.GetRawText() != "0") return false;
            }
            else if (field.Name is "firstPartyDomain" or "geckoViewSessionContextId" or "partitionKey")
            {
                if (field.Value.ValueKind != JsonValueKind.String || field.Value.GetString() != "") return false;
            }
            else return false;
        }
        return true;
    }
    private static BrowserCookieJar? Cookies(JsonElement root, DateTimeOffset now, Action check)
    {
        if (!Unique(root)) return null;
        if (!root.TryGetProperty("cookies", out var items)) return null;
        if (items.ValueKind != JsonValueKind.Array) throw new JsonException();
        if (items.GetArrayLength() > 4096) throw new LimitException();
        var cookies = new List<BrowserCookie>();
        foreach (var item in items.EnumerateArray())
        {
            check();
            if (item.ValueKind != JsonValueKind.Object) throw new JsonException();
            if (!Unique(item) || !IsDefaultContext(item)) continue;
            string? Text(string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var name = Text("name"); var value = Text("value"); var domain = Text("host") ?? Text("domain");
            if (name is null || !Names.Contains(name) || string.IsNullOrEmpty(value) || domain is null) continue;
            var normalized = domain.TrimStart('.');
            if (!normalized.Equals("xiaomimimo.com", StringComparison.OrdinalIgnoreCase) && !normalized.EndsWith(".xiaomimimo.com", StringComparison.OrdinalIgnoreCase)) continue;
            long expiry = 0;
            if (item.TryGetProperty("expires", out var expires) || item.TryGetProperty("expiry", out expires))
            {
                if (expires.ValueKind != JsonValueKind.Number || !expires.TryGetDouble(out var seconds) || !double.IsFinite(seconds)
                    || seconds < 0 || seconds > 253402300799) continue;
                expiry = (long)seconds;
                if (seconds > 0 && seconds <= now.ToUnixTimeSeconds()) continue;
            }
            var path = Text("path"); if (string.IsNullOrEmpty(path)) path = "/";
            var cookie = new BrowserCookie(name, value, domain, path,
                item.TryGetProperty("secure", out var secure) && secure.ValueKind == JsonValueKind.True, !domain.StartsWith('.'), expiry);
            try { _ = new BrowserCookieJar([cookie], Domains); }
            catch (InvalidDataException) { continue; }
            if (cookies.Any(existing => existing.Name == cookie.Name && existing.Domain.Equals(cookie.Domain, StringComparison.OrdinalIgnoreCase)
                && existing.Path == cookie.Path && existing != cookie)) return null;
            if (!cookies.Contains(cookie)) cookies.Add(cookie);
            if (cookies.Count > 512) throw new LimitException();
        }
        BrowserCookieJar jar;
        try { jar = new(cookies, Domains); }
        catch (InvalidDataException) { return null; }
        var header = SessionHeader(jar, now);
        // Conflicting duplicate names at different matching paths/domains are also
        // ambiguous account material; never select a token/user pair by list order.
        if (header is not null && header.Split(';').Select(pair => pair.Trim().Split('=', 2)).GroupBy(pair => pair[0], StringComparer.Ordinal)
            .Any(group => group.Select(pair => pair[1]).Distinct(StringComparer.Ordinal).Skip(1).Any())) return null;
        return jar;
    }
    private static string? SessionHeader(BrowserCookieJar jar, DateTimeOffset now)
    {
        try { return jar.Header(Target, now); }
        catch (InvalidDataException) { throw new LimitException(); }
    }
    private static bool Complete(string? header)
    {
        if (header is null) return false;
        var names = header.Split(';').Select(pair => pair.Trim().Split('=', 2)[0]).ToHashSet(StringComparer.Ordinal);
        return names.Contains("api-platform_serviceToken") && names.Contains("userId");
    }
}
