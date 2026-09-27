using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Services;

/// <summary>Read-only display metadata. A catalogue entry never changes a task's live state.</summary>
public static partial class CodexActivityCatalogue
{
    private sealed record Thread(string Id, string? Title, string? Name, string? AgentName, string? Cwd, string? ProjectId, string? Parent);
    public static IReadOnlyList<SessionActivity> Enrich(IReadOnlyList<SessionActivity> sessions, string statePath,
        CancellationToken cancellationToken = default) => Enrich(sessions, statePath, null, cancellationToken);
    public static IReadOnlyList<SessionActivity> Enrich(IReadOnlyList<SessionActivity> sessions, string statePath,
        string? desktopStore, CancellationToken cancellationToken = default)
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
            var project = columns.Contains("project_id") ? "substr(project_id,1,321)" : "NULL";
            var threads = new Dictionary<string, Thread>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = requested;
            for (var depth = 0; depth < 64 && pending.Length > 0; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                var lookups = LookupIds(pending);
                var parameters = lookups.Select((_, index) => "$id" + index).ToArray();
                command.CommandText = "SELECT id,substr(title,1,161),substr(cwd,1,4097)," + name + "," + nickname + "," + source + "," + project
                    + " FROM threads WHERE id IN (" + string.Join(",", parameters) + ") LIMIT 385";
                seen.UnionWith(pending);
                for (var index = 0; index < lookups.Length; index++) command.Parameters.AddWithValue(parameters[index], lookups[index]);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? Value(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
                    var id = Value(0); if (!ValidId(id)) continue;
                    var spawn = Spawn(Value(5), id!); var parent = spawn?.Parent;
                    if (!threads.TryAdd(id!, new(id!, Label(Value(1)), Label(Value(3)), Label(Value(4)) ?? spawn?.Nickname, Value(2), Value(6), parent)))
                        throw new InvalidDataException("Ambiguous local thread identity.");
                    if (parent is not null && !seen.Contains(parent)) next.Add(parent);
                }
                pending = next.Take(128).ToArray();
            }
            var projects = ProjectNames(connection, transaction, threads.Values.Select(x => x.ProjectId), cancellationToken);
            var titles = DesktopTitles(desktopStore, seen, cancellationToken);
            string Title(string id) => threads.TryGetValue(id, out var thread)
                ? thread.Name ?? titles.GetValueOrDefault(id) ?? thread.Title ?? thread.AgentName ?? "Task " + id[..8]
                : titles.GetValueOrDefault(id) ?? "Task " + id[..8];
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
                var title = Title(id);
                var projectName = thread.ProjectId is { } projectId ? projects.GetValueOrDefault(projectId) : null;
                return session with { Name = projectName ?? ProjectName(thread.Cwd, title), Detail = title,
                    ParentThreadId = parent, ParentThreadTitle = parent is null ? null : Title(parent) };
            }).ToArray();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or ArgumentException
            or InvalidOperationException or FormatException or OverflowException)
        { cancellationToken.ThrowIfCancellationRequested(); return sessions; }
    }

    private static bool ValidId(string? value) => Guid.TryParseExact(value, "D", out _);
    // Activity/parent references may use Guid's upper-case spelling while SQLite
    // stores the canonical lower-case spelling. Keep indexed equality lookups;
    // applying lower(id) or NOCASE to the column would force repeated full scans.
    private static string[] LookupIds(IEnumerable<string> ids) => ids
        .SelectMany(id => new[] { id, id.ToLowerInvariant(), id.ToUpperInvariant() }).Distinct(StringComparer.Ordinal).ToArray();
    private static string? Label(string? value)
    {
        value = value?.Trim();
        return value is { Length: > 0 and <= 160 } && !value.Any(char.IsControl)
            && value.ToLowerInvariant() is not ("codex" or ".codex" or "unknown project" or "/") ? value : null;
    }
    private static (string Parent, string? Nickname)? Spawn(string? source, string id)
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
            var nickname = spawn.TryGetProperty("agent_nickname", out var value) && value.ValueKind == JsonValueKind.String ? Label(value.GetString()) : null;
            return ValidId(result) && !string.Equals(result, id, StringComparison.OrdinalIgnoreCase) ? (result!, nickname) : null;
        }
        catch (JsonException) { return null; }
    }
}
