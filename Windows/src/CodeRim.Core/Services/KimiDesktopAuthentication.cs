using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Services;

/// <summary>Reads the explicitly selected Kimi Desktop plaintext session without decrypting browser data.</summary>
public static class KimiDesktopAuthentication
{
    private readonly record struct Stamp(bool Exists, long Length, long Modified, UsageScanner.FileIdentity? Identity)
    {
        public static Stamp Read(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? new(true, file.Length, file.LastWriteTimeUtc.Ticks, UsageScanner.FileIdentity.TryRead(path)) : default;
        }
    }

    public static KimiCredential Read(string path, DateTimeOffset now, CancellationToken token = default)
    {
        try { return ReadCore(path, now, token); }
        catch (Exception error) when (error is SqliteException or ArgumentException)
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidDataException("The selected Kimi Desktop database could not be read safely.", error);
        }
    }

    private static KimiCredential ReadCore(string path, DateTimeOffset now, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (path.Length is 0 or > 4096 || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || OperatingSystem.IsWindows() && path[Path.GetPathRoot(path)!.Length..].Contains(':'))
            throw new InvalidDataException("Select a local Kimi Desktop Cookies database.");
        path = Path.GetFullPath(path);
        SqliteReadSafety.ValidateFiles(path, 64 * 1024 * 1024);
        // Keep the database open without delete sharing while SQLite reads its own consistent snapshot.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var main = Stamp.Read(path); var wal = Stamp.Read(path + "-wal");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open();
        SqliteReadSafety.Configure(connection, token);
        using var transaction = connection.BeginTransaction(deferred: true);
        SqliteReadSafety.ValidateStoredTable(connection, transaction, "cookies", "host_key", "name", "value", "last_access_utc");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT typeof(value), length(CAST(value AS BLOB)),
                   CASE WHEN length(CAST(value AS BLOB)) <= 32768 THEN value ELSE NULL END,
                   typeof(last_access_utc), last_access_utc
            FROM main.cookies
            WHERE name = 'kimi-auth' COLLATE BINARY
              AND host_key COLLATE BINARY IN ('www.kimi.com','.www.kimi.com','.kimi.com','kimi.com')
            ORDER BY last_access_utc DESC LIMIT 2
            """;
        string? selected = null; double? expiry = null;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.IsDBNull(1) || reader.GetInt64(1) > 32768 || reader.GetString(0) != "text"
                    || reader.GetString(3) != "integer" || reader.GetInt64(4) < 0)
                    throw new InvalidDataException("The Kimi Desktop session format is not supported.");
                var accessed = reader.GetInt64(4);
                var raw = reader.GetString(2);
                selected = KimiAuthentication.Clean(raw);
                if (selected is not null && !CurrentToken(selected, now, out expiry)) selected = null;
                // Never revive an older token when the newest one is empty or expired.
                if (reader.Read() && (reader.GetString(3) != "integer" || reader.GetInt64(4) == accessed))
                    throw new InvalidDataException("The Kimi Desktop session is ambiguous.");
            }
        }
        transaction.Commit();
        token.ThrowIfCancellationRequested();
        SqliteReadSafety.ValidateFiles(path, 64 * 1024 * 1024);
        var finalWal = Stamp.Read(path + "-wal");
        // A read of an idle WAL-mode database can create an empty coordination WAL.
        // It contains no committed frames. Every pre-existing or nonempty WAL must remain identical.
        var unchangedWal = wal == finalWal || !wal.Exists && finalWal.Exists && finalWal.Length == 0;
        if (main != Stamp.Read(path) || !unchangedWal
            || UsageScanner.FileIdentity.TryRead(file.SafeFileHandle) != UsageScanner.FileIdentity.TryRead(path))
            throw new IOException("The Kimi Desktop session changed while reading. Retry the connection.");
        return new("web", selected, ExpiresAt: expiry, FilePath: path);
    }

    private static bool CurrentToken(string value, DateTimeOffset now, out double? expiry)
    {
        expiry = null;
        var parts = value.Split('.');
        if (parts.Length != 3) return true; // Opaque tokens are validated by the fixed provider endpoint.
        try
        {
            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            var text = new UTF8Encoding(false, true).GetString(bytes);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (root.EnumerateObject().Any(property => !names.Add(property.Name))) return false;
            if (!root.TryGetProperty("exp", out var exp)) return true;
            if (exp.ValueKind != JsonValueKind.Number || !exp.TryGetDecimal(out var seconds)
                || seconds <= now.ToUnixTimeMilliseconds() / 1000m || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return false;
            expiry = (double)seconds;
            return true;
        }
        catch (Exception error) when (error is JsonException or FormatException or DecoderFallbackException)
        {
            return false;
        }
    }
}
