using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Services;

/// <summary>Read-only display metadata. A catalogue entry never changes a task's live state.</summary>
public static class CodexActivityCatalogue
{
    private sealed record Thread(string Id, string Title, string? Project, string? Parent);
    public static IReadOnlyList<SessionActivity> Enrich(IReadOnlyList<SessionActivity> sessions, string statePath,
        CancellationToken cancellationToken = default)
    {
        var requested = sessions.Where(x => x.Provider == "codex" && x.RemoteHostId is null && ValidId(x.CodexThreadId))
            .Select(x => x.CodexThreadId!).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
        if (requested.Length == 0 || !File.Exists(statePath)) return sessions;
        try
        {
            SqliteReadSafety.ValidateFiles(statePath);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = statePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open(); SqliteReadSafety.Configure(connection, cancellationToken);
            using var transaction = connection.BeginTransaction(deferred: true);
            SqliteReadSafety.ValidateStoredTable(connection, transaction, "threads", "id", "title", "cwd");
            var columns = new HashSet<string>(StringComparer.Ordinal);
            using (var schema = connection.CreateCommand())
            {
                schema.Transaction = transaction; schema.CommandText = "PRAGMA main.table_xinfo('threads')";
                using var reader = schema.ExecuteReader();
                while (reader.Read()) if (reader.GetInt64(6) == 0) columns.Add(reader.GetString(1));
            }
            // Only fixed known stored columns enter the SQL string.
            var name = columns.Contains("name") ? "substr(name,1,161)" : "NULL";
            var nickname = columns.Contains("agent_nickname") ? "substr(agent_nickname,1,161)" : "NULL";
            var source = columns.Contains("source") ? "substr(source,1,8193)" : "NULL";
            var threads = new Dictionary<string, Thread>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = requested;
            for (var depth = 0; depth < 64 && pending.Length > 0; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                var parameters = pending.Select((_, index) => "$id" + index).ToArray();
                command.CommandText = "SELECT id,substr(title,1,161),substr(cwd,1,4097)," + name + "," + nickname + "," + source
                    + " FROM threads WHERE id IN (" + string.Join(",", parameters) + ") LIMIT 128";
                for (var index = 0; index < pending.Length; index++) { seen.Add(pending[index]); command.Parameters.AddWithValue(parameters[index], pending[index]); }
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? Value(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
                    var id = Value(0); if (!ValidId(id)) continue;
                    var parent = Parent(Value(5), id!);
                    var title = Label(Value(3)) ?? Label(Value(1)) ?? Label(Value(4)) ?? "Task " + id![..8];
                    var cwd = Value(2);
                    var project = cwd is { Length: <= 4096 } ? Label(cwd.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault()) : null;
                    threads.TryAdd(id!, new(id!, title, project, parent));
                    if (parent is not null && !seen.Contains(parent)) next.Add(parent);
                }
                pending = next.Take(128).ToArray();
            }
            string? MainParent(string id)
            {
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
                var current = threads[id].Parent;
                for (var depth = 0; current is not null && depth < 64; depth++)
                {
                    if (!visited.Add(current)) return null;
                    if (!threads.TryGetValue(current, out var ancestor) || ancestor.Parent is null) return current;
                    current = ancestor.Parent;
                }
                return null;
            }
            return sessions.Select(session =>
            {
                if (session.Provider != "codex" || session.RemoteHostId is not null || session.CodexThreadId is not { } id
                    || !threads.TryGetValue(id, out var thread)) return session;
                var parent = MainParent(id);
                return session with { Name = thread.Project ?? thread.Title, Detail = thread.Title,
                    ParentThreadId = parent, ParentThreadTitle = parent is null ? null : threads.GetValueOrDefault(parent)?.Title ?? "Task " + parent[..8] };
            }).ToArray();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or ArgumentException
            or InvalidOperationException or FormatException or OverflowException)
        { cancellationToken.ThrowIfCancellationRequested(); return sessions; }
    }

    private static bool ValidId(string? value) => Guid.TryParseExact(value, "D", out _);
    private static string? Label(string? value)
    {
        value = value?.Trim();
        return value is { Length: > 0 and <= 160 } && !value.Any(char.IsControl)
            && value.ToLowerInvariant() is not ("codex" or ".codex" or "unknown project" or "/") ? value : null;
    }
    private static string? Parent(string? source, string id)
    {
        if (source is not { Length: > 0 and <= 8192 }) return null;
        try
        {
            using var json = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 16 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("subagent", out var subagent)
                || subagent.ValueKind != JsonValueKind.Object || !subagent.TryGetProperty("thread_spawn", out var spawn)
                || spawn.ValueKind != JsonValueKind.Object || !spawn.TryGetProperty("parent_thread_id", out var parent)
                || parent.ValueKind != JsonValueKind.String) return null;
            var result = parent.GetString();
            return ValidId(result) && !string.Equals(result, id, StringComparison.OrdinalIgnoreCase) ? result : null;
        }
        catch (JsonException) { return null; }
    }
}
