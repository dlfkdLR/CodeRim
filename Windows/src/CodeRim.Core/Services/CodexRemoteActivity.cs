using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Services;

/// <summary>Recent desktop catalogue entries are discoverable tasks, never proof that they are still running.</summary>
public static class CodexRemoteActivity
{
    public static IReadOnlyList<SessionActivity> Read(string path, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return [];
        try
        {
            SqliteReadSafety.ValidateFiles(path);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open(); SqliteReadSafety.Configure(connection, cancellationToken);
            using var transaction = connection.BeginTransaction(deferred: true);
            SqliteReadSafety.ValidateStoredTable(connection, transaction, "local_thread_catalog", "host_id", "thread_id", "display_title", "cwd", "source_updated_at", "missing_candidate", "source_kind");
            SqliteReadSafety.ValidateStoredTable(connection, transaction, "local_thread_catalog_hosts", "host_id", "host_kind");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                SELECT substr(c.host_id,1,257), substr(c.thread_id,1,37), substr(c.display_title,1,161),
                       substr(c.cwd,1,4097), c.source_updated_at
                FROM local_thread_catalog c JOIN local_thread_catalog_hosts h ON h.host_id=c.host_id
                WHERE h.host_kind IN ('remote-control','ssh','wsl') AND c.missing_candidate=0
                  AND c.source_kind!='subagent' AND c.source_updated_at >= $after AND c.source_updated_at <= $now
                ORDER BY c.source_updated_at DESC,c.host_id,c.thread_id LIMIT 6
                """;
            command.Parameters.AddWithValue("$after", now.AddHours(-6).ToUnixTimeMilliseconds() / 1000d);
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds() / 1000d);
            using var reader = command.ExecuteReader(); var sessions = new List<SessionActivity>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var host = reader.IsDBNull(0) ? "" : reader.GetString(0); var thread = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (host is "" or "local" || host.Length > 256 || host.Any(char.IsControl) || !Guid.TryParseExact(thread, "D", out _)) continue;
                var title = reader.IsDBNull(2) ? null : Label(reader.GetString(2));
                var cwd = reader.IsDBNull(3) ? "" : reader.GetString(3); var project = CodexActivityCatalogue.ProjectName(cwd, "Remote task");
                if (!double.TryParse(Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    || !double.IsFinite(seconds) || seconds < now.AddHours(-6).ToUnixTimeSeconds() || seconds > now.ToUnixTimeMilliseconds() / 1000d) continue;
                sessions.Add(new("remote:" + host + ":" + thread, "codex", title ?? "Task " + thread[..8], "unavailable", DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)))
                    { CodexThreadId = thread, RemoteHostId = host, Detail = project });
            }
            return sessions;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { cancellationToken.ThrowIfCancellationRequested(); return []; }
    }
    private static string? Label(string value)
    {
        value = value.Trim();
        return value.Length is > 0 and <= 160 && !value.Any(char.IsControl) ? value : null;
    }
}
