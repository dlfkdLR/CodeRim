using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Services;

public sealed record BrowserCookie(string Name, string Value, string Domain, string Path,
    bool Secure, bool HostOnly, long ExpiresUnixSeconds);
public sealed record BrowserProfile(string Name, string Directory);

/// Imported cookies retain their browser scope. Never flatten different sites into one credential.
public sealed class BrowserCookieJar
{
    private readonly BrowserCookie[] cookies;
    private readonly string[] allowedDomains;
    private readonly string serialized;
    public BrowserCookieJar(IEnumerable<BrowserCookie> cookies, IEnumerable<string> allowedDomains)
    {
        this.allowedDomains = allowedDomains.Select(NormalizeDomain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        this.cookies = cookies.Take(513).ToArray();
        if (this.allowedDomains.Length == 0 || this.cookies.Length > 512
            || this.cookies.Any(c => !Valid(c) || !this.allowedDomains.Any(d => DomainWithin(c.Domain.TrimStart('.'), d))))
            throw new InvalidDataException("The browser cookie collection is invalid.");
        serialized = JsonSerializer.Serialize(this.cookies);
        if (serialized.Length > 262144) throw new InvalidDataException("The browser cookie collection is too large.");
    }
    public int Count => cookies.Length;
    public string Serialize() => serialized;
    public static BrowserCookieJar Parse(string json, IEnumerable<string> domains)
    {
        if (json.Length > 262144) throw new InvalidDataException("The browser cookie collection is too large.");
        return new(JsonSerializer.Deserialize<BrowserCookie[]>(json) ?? throw new InvalidDataException(), domains);
    }
    public string? Header(Uri target, DateTimeOffset now)
    {
        if (target.Scheme != Uri.UriSchemeHttps || target.UserInfo.Length > 0
            || !allowedDomains.Any(d => DomainWithin(target.IdnHost, d))) return null;
        var pairs = cookies.Where(c => c.ExpiresUnixSeconds == 0 || c.ExpiresUnixSeconds > now.ToUnixTimeSeconds())
            .Where(c => c.HostOnly ? target.IdnHost.Equals(c.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
                : DomainWithin(target.IdnHost, c.Domain.TrimStart('.')))
            .Where(c => PathMatches(target.AbsolutePath, c.Path))
            .OrderByDescending(c => c.Path.Length)
            .DistinctBy(c => (c.Name, c.Domain, c.Path))
            .Select(c => c.Name + "=" + c.Value);
        var header = string.Join("; ", pairs);
        if (header.Length > 65536) throw new InvalidDataException("The cookie header is too large.");
        return header.Length == 0 ? null : header;
    }
    public string? FirstHeader(DateTimeOffset now) => allowedDomains
        .Select(d => Header(new Uri("https://" + d + "/"), now)).FirstOrDefault(h => h is not null);
    public static string NormalizeDomain(string domain)
    {
        var value = domain.Trim().TrimStart('.').ToLowerInvariant();
        if (value.Length is 0 or > 253 || Uri.CheckHostName(value) != UriHostNameType.Dns
            || !value.Contains('.') || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-'))
            throw new InvalidDataException("Invalid provider cookie domain.");
        return value;
    }
    private static bool DomainWithin(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    private static bool PathMatches(string request, string cookie) => request == cookie
        || request.StartsWith(cookie, StringComparison.Ordinal) && (cookie.EndsWith('/') || request.Length > cookie.Length && request[cookie.Length] == '/');
    private static bool ValidDomain(string domain)
    {
        var host = domain.StartsWith('.') ? domain[1..] : domain;
        try { return host.Equals(NormalizeDomain(host), StringComparison.OrdinalIgnoreCase); }
        catch (InvalidDataException) { return false; }
    }
    private static bool Valid(BrowserCookie c) => c is not null && c.Name is { Length: > 0 and <= 256 }
        && c.Name.All(x => char.IsAsciiLetterOrDigit(x) || x == (char)96 || "!#$%&'*+-.^_|~".Contains(x))
        && c.Value is { Length: <= 32768 } && c.Value.All(x => x >= 0x21 && x <= 0x7e && x is not '"' and not ',' and not ';' and not '\\')
        && c.Domain is { Length: > 0 and <= 254 } && ValidDomain(c.Domain) && c.Path is { Length: > 0 and <= 4096 }
        && c.Path.StartsWith('/') && !c.Path.Any(char.IsControl) && c.ExpiresUnixSeconds >= 0;
}

public static class FirefoxCookieImport
{
    public static IReadOnlyList<BrowserProfile> Profiles(string firefoxRoot)
    {
        var ini = Path.Combine(firefoxRoot, "profiles.ini");
        if (!File.Exists(ini) || new FileInfo(ini).Length > 65536 || HasLink(ini)) return [];
        var profiles = new List<BrowserProfile>();
        Dictionary<string, string>? section = null;
        void Add()
        {
            if (section is null || !section.TryGetValue("Path", out var path)) return;
            var directory = section.GetValueOrDefault("IsRelative") == "1"
                ? Path.GetFullPath(Path.Combine(firefoxRoot, path.Replace('/', Path.DirectorySeparatorChar)))
                : Path.GetFullPath(path);
            var root = Path.GetFullPath(firefoxRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || HasLink(directory) || !File.Exists(Path.Combine(directory, "cookies.sqlite"))) return;
            profiles.Add(new(section.GetValueOrDefault("Name") ?? Path.GetFileName(directory), directory));
        }
        foreach (var raw in File.ReadLines(ini))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { Add(); section = line.StartsWith("[Profile", StringComparison.Ordinal) ? new(StringComparer.Ordinal) : null; }
            else if (section is not null && line.IndexOf('=') is var at && at > 0) section[line[..at]] = line[(at + 1)..];
        }
        Add(); return profiles.DistinctBy(p => p.Directory).Take(32).ToArray();
    }
    public static BrowserCookieJar Read(BrowserProfile profile, IEnumerable<string> allowedDomains, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var domains = allowedDomains.Select(BrowserCookieJar.NormalizeDomain).Distinct().ToArray();
        if (domains.Length is 0 or > 16) throw new InvalidDataException("Invalid provider domain list.");
        var path = Path.Combine(profile.Directory, "cookies.sqlite");
        if (!File.Exists(path) || new FileInfo(path).Length > 128 * 1024 * 1024 || HasLink(path))
            throw new InvalidDataException("The Firefox cookie database is unavailable.");
        SqliteReadSafety.ValidateFiles(path);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
        connection.Open();
        SqliteReadSafety.Configure(connection, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var schema = connection.CreateCommand(); schema.Transaction = transaction; schema.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(schema.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        // Firefox schema 16 changes expiry from seconds to milliseconds; 17 adds updateTime.
        if (version is < 7 or > 17) throw new InvalidDataException("This Firefox cookie schema is not supported.");
        SqliteReadSafety.ValidateStoredTable(connection, transaction, "moz_cookies", "name", "value", "host", "path", "isSecure", "expiry", "originAttributes");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var conditions = domains.Select((_, i) => $"(host = $d{i} OR host = '.' || $d{i} OR host LIKE '%.' || $d{i})");
        command.CommandText = "SELECT name,value,host,path,isSecure,expiry,length(CAST(name AS BLOB)),length(CAST(value AS BLOB)),length(CAST(host AS BLOB)),length(CAST(path AS BLOB)) FROM main.moz_cookies WHERE originAttributes='' AND (" +
            string.Join(" OR ", conditions) + ") AND (expiry=0 OR expiry>$now) LIMIT 513";
        for (var i = 0; i < domains.Length; i++) command.Parameters.AddWithValue("$d" + i, domains[i]);
        command.Parameters.AddWithValue("$now", version >= 16 ? now.ToUnixTimeMilliseconds() : now.ToUnixTimeSeconds());
        var cookies = new List<BrowserCookie>(); long totalBytes = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Enumerable.Range(0, 10).Any(reader.IsDBNull) || reader.GetInt64(6) > 256 || reader.GetInt64(7) > 32768
                    || reader.GetInt64(8) > 254 || reader.GetInt64(9) > 16384)
                    throw new InvalidDataException("A browser cookie exceeds its safety limit.");
                totalBytes += reader.GetInt64(6) + reader.GetInt64(7) + reader.GetInt64(8) + reader.GetInt64(9);
                if (totalBytes > 262144) throw new InvalidDataException("The browser cookie collection is too large.");
                var domain = reader.GetString(2);
                var expiry = reader.GetInt64(5);
                cookies.Add(new(reader.GetString(0), reader.GetString(1), domain, reader.GetString(3),
                    reader.GetInt64(4) != 0, !domain.StartsWith('.'), version >= 16 ? expiry / 1000 : expiry));
            }
        }
        transaction.Commit();
        return new(cookies, domains);
    }
    private static bool HasLink(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
}
