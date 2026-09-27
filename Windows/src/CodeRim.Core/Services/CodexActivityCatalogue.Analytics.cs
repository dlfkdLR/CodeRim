using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Services;

public sealed record CodexAnalyticsLabel(string? SessionTitle, string ProjectName);

public static partial class CodexActivityCatalogue
{
    /// <summary>In-memory labels for existing opaque analytics identities; never writes titles into history.</summary>
    public static IReadOnlyDictionary<string, CodexAnalyticsLabel> ReadAnalyticsLabels(IReadOnlyCollection<string> sessionIds,
        string statePath, string? desktopStore = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = sessionIds.Where(x => x.Length == 64 && x.All(char.IsAsciiHexDigit)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, CodexAnalyticsLabel>(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0 || !File.Exists(statePath)) return result;
        try
        {
            SqliteReadSafety.ValidateFiles(statePath);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = statePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open(); SqliteReadSafety.Configure(connection, cancellationToken);
            using var transaction = connection.BeginTransaction(deferred: true);
            SqliteReadSafety.ValidateStoredTable(connection, transaction, "threads", "id", "title", "cwd", "rollout_path");
            var columns = new HashSet<string>(StringComparer.Ordinal);
            using (var schema = connection.CreateCommand())
            {
                schema.Transaction = transaction; schema.CommandText = "PRAGMA main.table_xinfo('threads')";
                using var reader = schema.ExecuteReader();
                while (reader.Read()) if (reader.GetInt64(6) == 0) columns.Add(reader.GetString(1));
            }
            string Field(string name, int limit) => columns.Contains(name) ? "substr(" + name + ",1," + limit.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : "NULL";
            var matches = new List<(string Key, Thread Thread)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                // Includes archived tasks. Both rows and SQLite VM/time are bounded;
                // optional names must not turn local history into an unbounded query.
                command.CommandText = "SELECT substr(id,1,37),substr(title,1,161),substr(cwd,1,4097),"
                    + Field("name", 161) + "," + Field("agent_nickname", 161) + "," + Field("project_id", 321) + "," + Field("source", 8193)
                    + " FROM threads LIMIT 50001";
                using var reader = command.ExecuteReader(); var rows = 0;
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++rows > 50000) return result;
                    string? Value(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
                    var id = Value(0); if (!ValidId(id)) continue;
                    var keys = new[] { id!, id!.ToLowerInvariant(), id!.ToUpperInvariant() }.Distinct(StringComparer.Ordinal)
                        .Select(Parsing.ClaudeJsonlParser.Hash).Where(requested.Contains).ToArray();
                    if (keys.Length == 0) continue;
                    if (!seen.Add(id!)) throw new InvalidDataException("Ambiguous analytics thread identity.");
                    var thread = new Thread(id!, Label(Value(1)), Label(Value(3)), Label(Value(4)) ?? Spawn(Value(6), id!)?.Nickname, Value(2), Value(5), null);
                    matches.AddRange(keys.Select(key => (key, thread)));
                }
            }
            var projects = ProjectNames(connection, transaction, matches.Select(x => x.Thread.ProjectId), cancellationToken);
            var titles = DesktopTitles(desktopStore, matches.Select(x => x.Thread.Id), cancellationToken);
            foreach (var (key, thread) in matches)
            {
                var title = thread.Name ?? titles.GetValueOrDefault(thread.Id) ?? thread.Title;
                var fallback = title ?? thread.AgentName ?? "Task " + thread.Id[..8];
                var project = thread.ProjectId is { } id ? projects.GetValueOrDefault(id) : null;
                result[key] = new(title, project ?? ProjectName(thread.Cwd, fallback));
            }
            return result;
        }
        catch (Exception error) when (OptionalMetadataFailure(error))
        { cancellationToken.ThrowIfCancellationRequested(); return new Dictionary<string, CodexAnalyticsLabel>(StringComparer.OrdinalIgnoreCase); }
    }
}
