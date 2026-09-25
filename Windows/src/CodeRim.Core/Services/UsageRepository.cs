using System.Globalization;
using CodeRim.Core.Domain;
using CodeRim.Core.Parsing;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Services;

/// Durable numeric history. No credentials, full paths, prompts or response bodies.
public sealed class UsageRepository
{
    private readonly string connectionString;
    private readonly string databasePath;
    public UsageRepository(string path)
    {
        databasePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS events (
                provider TEXT NOT NULL, id TEXT NOT NULL, time INTEGER NOT NULL,
                input INTEGER NOT NULL, cached INTEGER NOT NULL, output INTEGER NOT NULL, written INTEGER,
                model TEXT NOT NULL, project TEXT NOT NULL, session TEXT NOT NULL, projectId TEXT NOT NULL,
                PRIMARY KEY(provider,id));
            CREATE INDEX IF NOT EXISTS events_date ON events(provider,time);
            CREATE INDEX IF NOT EXISTS events_session ON events(provider,session,time);
            CREATE TABLE IF NOT EXISTS exclusions(provider TEXT NOT NULL, id TEXT NOT NULL, PRIMARY KEY(provider,id));
            CREATE TABLE IF NOT EXISTS cutoffs (provider TEXT PRIMARY KEY, time INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS session_links(provider TEXT NOT NULL, id TEXT NOT NULL, parentId TEXT, PRIMARY KEY(provider,id));
            CREATE TABLE IF NOT EXISTS session_metadata(provider TEXT NOT NULL, id TEXT NOT NULL,
                started INTEGER, project TEXT, projectTime INTEGER, PRIMARY KEY(provider,id));
            CREATE TABLE IF NOT EXISTS attachments(provider TEXT NOT NULL, id TEXT NOT NULL, session TEXT NOT NULL,
                time INTEGER NOT NULL, count INTEGER NOT NULL, PRIMARY KEY(provider,id));
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<UsageEvent> Merge(string provider, IEnumerable<UsageEvent> events, IEnumerable<SessionDetails>? sessions = null)
        => Write(provider, events, sessions, replace: false);

    /// Replaces derived statistics atomically; cleared history remains excluded.
    public IReadOnlyList<UsageEvent> Rebuild(string provider, IEnumerable<UsageEvent> events, IEnumerable<SessionDetails>? sessions = null)
        => Write(provider, events, sessions, replace: true);

    private List<UsageEvent> Write(string provider, IEnumerable<UsageEvent> events, IEnumerable<SessionDetails>? sessions, bool replace)
    {
        ArgumentNullException.ThrowIfNull(events);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (replace)
        {
            command.CommandText = "DELETE FROM events WHERE provider=$provider; DELETE FROM attachments WHERE provider=$provider; DELETE FROM session_links WHERE provider=$provider; DELETE FROM session_metadata WHERE provider=$provider";
            command.Parameters.AddWithValue("$provider", provider); command.ExecuteNonQuery(); command.Parameters.Clear();
        }
        // Inputs are disjoint in storage so revisions from copied Claude histories cannot lose cache reads/writes.
        command.CommandText = """
            INSERT INTO events(provider,id,time,input,cached,output,written,model,project,session,projectId)
            SELECT $provider,$id,$time,$input,$cached,$output,$written,$model,$project,$session,$projectId
            WHERE NOT EXISTS(SELECT 1 FROM exclusions WHERE provider=$provider AND id=$id) AND $time > COALESCE((SELECT time FROM cutoffs WHERE provider=$provider),-1)
            ON CONFLICT(provider,id) DO UPDATE SET
                time=MIN(time,excluded.time), input=CASE WHEN provider='claude' THEN MAX(input,excluded.input)
                    ELSE MAX(input+cached+COALESCE(written,0),excluded.input+excluded.cached+COALESCE(excluded.written,0))
                    - MAX(cached,excluded.cached) - MAX(COALESCE(written,0),COALESCE(excluded.written,0)) END,
                cached=MAX(cached,excluded.cached),
                output=MAX(output,excluded.output), written=CASE WHEN written IS NULL THEN excluded.written WHEN excluded.written IS NULL THEN written ELSE MAX(written,excluded.written) END;
            """;
        foreach (var usageEvent in events)
        {
            if (usageEvent.Provider != provider || !usageEvent.Usage.IsValid) continue;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$provider", provider);
            command.Parameters.AddWithValue("$id", usageEvent.EventKey);
            command.Parameters.AddWithValue("$time", usageEvent.OccurredAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$input", usageEvent.Usage.UncachedInputTokens);
            command.Parameters.AddWithValue("$cached", usageEvent.Usage.CachedInputTokens);
            command.Parameters.AddWithValue("$output", usageEvent.Usage.OutputTokens);
            command.Parameters.AddWithValue("$written", (object?)usageEvent.Usage.CacheWriteInputTokens ?? DBNull.Value);
            command.Parameters.AddWithValue("$model", usageEvent.Model);
            command.Parameters.AddWithValue("$project", usageEvent.Project);
            command.Parameters.AddWithValue("$session", usageEvent.SessionId);
            command.Parameters.AddWithValue("$projectId", usageEvent.ProjectId);
            command.ExecuteNonQuery();
        }
        foreach (var session in sessions ?? [])
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO session_links VALUES($provider,$id,$parent) ON CONFLICT(provider,id) DO UPDATE SET parentId=COALESCE(excluded.parentId,parentId)";
            command.Parameters.AddWithValue("$provider", provider); command.Parameters.AddWithValue("$id", session.Id);
            command.Parameters.AddWithValue("$parent", (object?)session.ParentId ?? DBNull.Value);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO session_metadata(provider,id,started,project,projectTime)
                VALUES($provider,$id,CASE WHEN $provider='claude' THEN
                    (SELECT MIN(time) FROM events WHERE provider=$provider AND session=$id) ELSE $started END,
                    CASE WHEN $provider='claude' THEN
                        (SELECT project FROM events WHERE provider=$provider AND session=$id AND projectId!='unknown' ORDER BY time DESC,project ASC LIMIT 1) ELSE $project END,
                    CASE WHEN $provider='claude' THEN
                        (SELECT MAX(time) FROM events WHERE provider=$provider AND session=$id AND projectId!='unknown') ELSE $projectTime END)
                ON CONFLICT(provider,id) DO UPDATE SET
                    started=CASE WHEN provider='claude' AND started IS NOT NULL AND excluded.started IS NOT NULL
                        THEN MIN(started,excluded.started) ELSE COALESCE(started,excluded.started) END,
                    project=CASE WHEN excluded.project IS NOT NULL AND (project IS NULL OR
                        COALESCE(excluded.projectTime,-9223372036854775808)>COALESCE(projectTime,-9223372036854775808) OR
                        COALESCE(excluded.projectTime,-9223372036854775808)=COALESCE(projectTime,-9223372036854775808) AND excluded.project<project)
                        THEN excluded.project ELSE project END,
                    projectTime=CASE WHEN excluded.project IS NOT NULL AND (project IS NULL OR
                        COALESCE(excluded.projectTime,-9223372036854775808)>COALESCE(projectTime,-9223372036854775808))
                        THEN excluded.projectTime ELSE projectTime END;
                """;
            command.Parameters.AddWithValue("$provider", provider); command.Parameters.AddWithValue("$id", session.Id);
            command.Parameters.AddWithValue("$started", (object?)session.StartedAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            command.Parameters.AddWithValue("$project", (object?)session.ProjectName ?? DBNull.Value);
            command.Parameters.AddWithValue("$projectTime", (object?)session.ProjectObservedAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            command.ExecuteNonQuery();
            foreach (var attachment in session.Attachments)
            {
                if (attachment.Count <= 0) continue;
                command.CommandText = """
                    INSERT INTO attachments(provider,id,session,time,count)
                    SELECT $provider,$id,$session,$time,$count
                    WHERE NOT EXISTS(SELECT 1 FROM exclusions WHERE provider=$provider AND id=$id)
                        AND $time > COALESCE((SELECT time FROM cutoffs WHERE provider=$provider),-1)
                    ON CONFLICT(provider,id) DO UPDATE SET count=MAX(count,excluded.count);
                    """;
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$provider", provider); command.Parameters.AddWithValue("$id", attachment.Id);
                command.Parameters.AddWithValue("$session", session.Id); command.Parameters.AddWithValue("$time", attachment.OccurredAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$count", attachment.Count); command.ExecuteNonQuery();
            }
        }
        var result = Read(provider, connection, transaction);
        transaction.Commit();
        return result;
    }

    public IReadOnlyList<UsageEvent> Read(string provider)
    {
        using var connection = Open();
        return Read(provider, connection);
    }
    private static List<UsageEvent> Read(string provider, SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,time,input,cached,output,written,model,project,session,projectId FROM events WHERE provider=$provider ORDER BY time";
        command.Parameters.AddWithValue("$provider", provider);
        using var reader = command.ExecuteReader();
        var result = new List<UsageEvent>();
        while (reader.Read()) result.Add(new UsageEvent(reader.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            new TokenUsage(reader.GetInt64(2) + reader.GetInt64(3) + (reader.IsDBNull(5) ? 0 : reader.GetInt64(5)), reader.GetInt64(3), reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetInt64(5)),
            reader.GetString(6), reader.GetString(7), reader.GetString(8), provider, reader.GetString(9)));
        return result;
    }

    public IReadOnlyList<SessionDetails> ReadSessionDetails(string provider)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT links.id,links.parentId,metadata.started,metadata.project,metadata.projectTime FROM session_links links LEFT JOIN session_metadata metadata ON metadata.provider=links.provider AND metadata.id=links.id WHERE links.provider=$provider";
        command.Parameters.AddWithValue("$provider", provider);
        var links = new List<SessionDetails>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) links.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), []) {
                StartedAt = reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                ProjectName = reader.IsDBNull(3) ? null : reader.GetString(3),
                ProjectObservedAt = reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)) });
        command.CommandText = "SELECT id,session,time,count FROM attachments WHERE provider=$provider";
        var images = new Dictionary<string, List<AttachmentObservation>>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var session = reader.GetString(1);
                if (!images.TryGetValue(session, out var list)) images[session] = list = [];
                list.Add(new AttachmentObservation(reader.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)), reader.GetInt32(3)));
            }
        return links.Select(x => x with { Attachments = images.GetValueOrDefault(x.Id) ?? [] }).ToArray();
    }

    public void Clear(string provider, DateTimeOffset cutoff)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO exclusions SELECT provider,id FROM events WHERE provider=$provider; INSERT OR IGNORE INTO exclusions SELECT provider,id FROM attachments WHERE provider=$provider; DELETE FROM events WHERE provider=$provider; DELETE FROM attachments WHERE provider=$provider; DELETE FROM session_links WHERE provider=$provider; DELETE FROM session_metadata WHERE provider=$provider; INSERT INTO cutoffs VALUES($provider,$time) ON CONFLICT(provider) DO UPDATE SET time=excluded.time";
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$time", cutoff.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public LocalDataStatistics Statistics(string provider)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MIN(time), MAX(time) FROM events WHERE provider=$provider";
        command.Parameters.AddWithValue("$provider", provider); using var reader = command.ExecuteReader(); reader.Read();
        long bytes = 0;
        foreach (var path in new[] { databasePath, databasePath + "-wal" })
            if (File.Exists(path)) bytes = checked(bytes + new FileInfo(path).Length);
        return new(bytes, reader.GetInt64(0), reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}

public sealed record LocalDataStatistics(long DatabaseBytes, long RecordCount, DateTimeOffset? OldestRecord, DateTimeOffset? NewestRecord);
