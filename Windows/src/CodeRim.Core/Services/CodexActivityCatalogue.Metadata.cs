using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Services;

public static partial class CodexActivityCatalogue
{
    internal static string ProjectName(string? cwd, string fallback)
    {
        if (cwd is not { Length: > 0 and <= 4096 } || cwd.Any(char.IsControl)) return fallback;
        var parts = cwd.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4 && parts[^4].Equals("Documents", StringComparison.OrdinalIgnoreCase)
            && parts[^3].Equals("Codex", StringComparison.OrdinalIgnoreCase) && IsDateFolder(parts[^2])) return "";
        return Label(parts.LastOrDefault()) ?? fallback;
    }
    private static bool IsDateFolder(string value) => value.Length == 10 && value[4] == '-' && value[7] == '-'
        && value.Where((_, index) => index is not (4 or 7)).All(char.IsAsciiDigit);

    private static Dictionary<string, string> ProjectNames(SqliteConnection connection, SqliteTransaction transaction,
        IEnumerable<string?> projectIds, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = projectIds.Where(x => x is { Length: > 0 and <= 320 } && !x.Any(char.IsControl))
            .Select(x => x!).Distinct(StringComparer.Ordinal).Take(8192).ToArray();
        if (ids.Length == 0) return result;
        try
        {
            SqliteReadSafety.ValidateStoredTable(connection, transaction, "projects", "id", "name");
            foreach (var batch in ids.Chunk(128))
            {
                token.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                var parameters = batch.Select((_, index) => "$project" + index).ToArray();
                command.CommandText = "SELECT substr(id,1,321),substr(name,1,161) FROM projects WHERE id IN (" + string.Join(",", parameters) + ") LIMIT 128";
                for (var index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(parameters[index], batch[index]);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (!reader.IsDBNull(0) && !reader.IsDBNull(1) && Label(reader.GetString(1)) is { } name)
                        result.TryAdd(reader.GetString(0), name);
                }
            }
        }
        catch (Exception error) when (OptionalMetadataFailure(error)) { token.ThrowIfCancellationRequested(); }
        return result;
    }
    private static Dictionary<string, string> DesktopTitles(string? path, IEnumerable<string> threadIds, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (path is null || !File.Exists(path)) return result;
        try
        {
            SqliteReadSafety.ValidateFiles(path);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open(); SqliteReadSafety.Configure(connection, token);
            using var transaction = connection.BeginTransaction(deferred: true);
            SqliteReadSafety.ValidateStoredTable(connection, transaction, "local_thread_catalog", "host_id", "thread_id", "display_title", "source_updated_at");
            foreach (var batch in threadIds.Distinct(StringComparer.OrdinalIgnoreCase).Take(8192).Chunk(128))
            {
                token.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                var lookups = LookupIds(batch);
                var parameters = lookups.Select((_, index) => "$thread" + index).ToArray();
                // A remote catalogue copy must never rename a local task with a colliding UUID.
                command.CommandText = "SELECT substr(thread_id,1,37),substr(display_title,1,161) FROM local_thread_catalog WHERE host_id='local' AND thread_id IN ("
                    + string.Join(",", parameters) + ") ORDER BY source_updated_at DESC LIMIT 1024";
                for (var index = 0; index < lookups.Length; index++) command.Parameters.AddWithValue(parameters[index], lookups[index]);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (!reader.IsDBNull(0) && !reader.IsDBNull(1) && ValidId(reader.GetString(0)) && Label(reader.GetString(1)) is { } title)
                        result.TryAdd(reader.GetString(0), title);
                }
            }
        }
        catch (Exception error) when (OptionalMetadataFailure(error)) { token.ThrowIfCancellationRequested(); }
        return result;
    }
    private static bool OptionalMetadataFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or SqliteException or ArgumentException or InvalidOperationException or FormatException or OverflowException;
}
