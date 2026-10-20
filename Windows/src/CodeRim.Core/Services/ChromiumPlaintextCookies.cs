using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Services;

/// <summary>Only plaintext cookies from one explicitly selected, closed Chromium profile.
/// Protected cookies and uncheckpointed WALs are unsupported; no browser decryption or source writes.</summary>
public static class ChromiumPlaintextCookies
{
    private const long MaximumBytes = 64 * 1024 * 1024;
    public static BrowserCookieJar Read(string profileDirectory, string domain, DateTimeOffset now, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var profile = ChromiumProviderAuthentication.Profile(profileDirectory);
        domain = BrowserCookieJar.NormalizeDomain(domain);
        if (domain is not "minimax.io" and not "minimaxi.com") throw new InvalidDataException("Unsupported cookie domain.");
        var candidates = new[] { Path.Combine(profile, "Network", "Cookies"), Path.Combine(profile, "Cookies") }.Where(File.Exists).ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("Select a profile with one supported plaintext Cookies database.");
        var path = candidates[0]; SqliteReadSafety.ValidateFiles(path, MaximumBytes);
        static long Length(string name) => File.Exists(name) ? new FileInfo(name).Length : -1;
        if (Length(path + "-wal") > 0 || Length(path + "-journal") > 0)
            throw new IOException("Close the selected browser so its cookie database can be read safely.");
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(path + suffix) && (File.GetAttributes(path + suffix) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked cookie databases are unsupported.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var identity = UsageScanner.FileIdentity.TryRead(file.SafeFileHandle); var size = file.Length;
        byte[] Hash()
        {
            file.Position = 0; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var bytes = new byte[16384]; long total = 0; int count;
            while ((count = file.Read(bytes)) > 0) { token.ThrowIfCancellationRequested(); total += count; if (total > MaximumBytes || total != file.Position) throw new InvalidDataException("Cookie database exceeded its read limit."); hash.AppendData(bytes, 0, count); }
            return hash.GetHashAndReset();
        }
        string?[] Names()
        {
            var names = Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Take(513).Select(Path.GetFileName).ToArray();
            if (names.Length > 512) throw new InvalidDataException("The cookie directory has too many entries.");
            return names.Order(StringComparer.Ordinal).ToArray();
        }
        var originalHash = Hash(); var originalNames = Names();
        var cookies = new List<BrowserCookie>(); var seen = new HashSet<(string Name, string Domain, string Path)>(); long totalBytes = 0; var rows = 0;
        try
        {
            // immutable prevents SQLite from creating coordination WAL/SHM files in the browser profile.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = new Uri(path).AbsoluteUri + "?immutable=1",
                Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open(); SqliteReadSafety.Configure(connection, token); using var transaction = connection.BeginTransaction(deferred: true);
            SqliteReadSafety.ValidateStoredTable(connection, transaction, "meta", "key", "value");
            using (var schema = connection.CreateCommand())
            {
                schema.Transaction = transaction; schema.CommandText = "SELECT value FROM main.meta WHERE key='version' COLLATE BINARY LIMIT 2";
                using var version = schema.ExecuteReader();
                if (!version.Read() || version.GetString(0) != "24" || version.Read()) throw new InvalidDataException("Only the verified Chromium cookie schema 24 is supported.");
            }
            SqliteReadSafety.ValidateStoredTable(connection, transaction, "cookies", "host_key", "name", "value", "encrypted_value", "path", "is_secure", "expires_utc", "has_expires", "top_frame_site_key", "has_cross_site_ancestor");
            using var query = connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = """
                SELECT typeof(name), length(CAST(name AS BLOB)), CASE WHEN length(CAST(name AS BLOB))<=256 THEN name END,
                       typeof(value), length(CAST(value AS BLOB)), CASE WHEN length(CAST(value AS BLOB))<=32768 THEN value END,
                       typeof(host_key), length(CAST(host_key AS BLOB)), CASE WHEN length(CAST(host_key AS BLOB))<=254 THEN host_key END,
                       typeof(path), length(CAST(path AS BLOB)), CASE WHEN length(CAST(path AS BLOB))<=4096 THEN path END,
                       typeof(is_secure), is_secure, typeof(expires_utc), expires_utc, typeof(has_expires), has_expires,
                       length(encrypted_value), typeof(encrypted_value)
                FROM main.cookies
                WHERE top_frame_site_key='' AND has_cross_site_ancestor=0
                  AND (host_key=$domain OR host_key='.'||$domain OR host_key LIKE '%.'||$domain)
                LIMIT 513
                """;
            query.Parameters.AddWithValue("$domain", domain);
            using (var reader = query.ExecuteReader())
            {
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (++rows > 512 || Enumerable.Range(0, 20).Any(reader.IsDBNull)
                        || reader.GetString(0) != "text" || reader.GetInt64(1) > 256 || reader.GetString(3) != "text" || reader.GetInt64(4) > 32768
                        || reader.GetString(6) != "text" || reader.GetInt64(7) > 254 || reader.GetString(9) != "text" || reader.GetInt64(10) > 4096
                        || reader.GetString(12) != "integer" || reader.GetInt64(13) is not (0 or 1)
                        || reader.GetString(14) != "integer" || reader.GetInt64(15) < 0 || reader.GetString(16) != "integer" || reader.GetInt64(17) is not (0 or 1))
                        throw new InvalidDataException("The selected cookie database contains unsupported fields.");
                    if (reader.GetString(19) != "blob") throw new InvalidDataException("Unsupported cookie encryption field.");
                    if (reader.GetInt64(18) != 0) throw new InvalidDataException("This profile uses protected cookies. CodeRim does not decrypt them. Use a manual Web session or Firefox instead.");
                    var name = reader.GetString(2); var value = reader.GetString(5); var host = reader.GetString(8); var cookiePath = reader.GetString(11);
                    totalBytes += reader.GetInt64(1) + reader.GetInt64(4) + reader.GetInt64(7) + reader.GetInt64(10);
                    if (totalBytes > 262144 || !seen.Add((name, host, cookiePath))) throw new InvalidDataException("The selected cookie session is too large or ambiguous.");
                    var expiry = reader.GetInt64(17) == 0 ? 0 : reader.GetInt64(15) / 1000000 - 11644473600;
                    if (reader.GetInt64(17) != 0 && expiry <= now.ToUnixTimeSeconds()) continue;
                    cookies.Add(new(name, value, host, cookiePath, reader.GetInt64(13) == 1, !host.StartsWith('.'), expiry));
                }
            }
            transaction.Commit();
        }
        catch (SqliteException) { token.ThrowIfCancellationRequested(); throw new InvalidDataException("The selected cookie database could not be read safely."); }
        token.ThrowIfCancellationRequested(); SqliteReadSafety.ValidateFiles(path, MaximumBytes);
        if (file.Length != size || identity != UsageScanner.FileIdentity.TryRead(path) || !CryptographicOperations.FixedTimeEquals(originalHash, Hash())
            || !originalNames.SequenceEqual(Names()) || Length(path + "-wal") > 0 || Length(path + "-journal") > 0)
            throw new IOException("The selected cookie database changed. Retry after closing the browser.");
        return new(cookies, [domain]);
    }
}
