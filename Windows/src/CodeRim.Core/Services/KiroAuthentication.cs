using System.Text.Json;
using Microsoft.Data.Sqlite;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Services;
public static class KiroAuthentication
{
    public static string? Read(string database)
    {
        if (!Path.IsPathFullyQualified(database) || !File.Exists(database)) return null;
        if (database.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        SqliteReadSafety.ValidateFiles(database, 256 * 1024 * 1024);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open(); SqliteReadSafety.Configure(connection);
        using var transaction = connection.BeginTransaction(deferred: true);
        SqliteReadSafety.ValidateStoredTable(connection, transaction, "auth_kv", "key", "value");
        SqliteReadSafety.ValidateStoredTable(connection, transaction, "state", "key", "value");
        string? Value(string sql)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
            return command.ExecuteScalar() is string value && value.Length <= 262144 ? value : null;
        }
        var token = Value("SELECT value FROM main.auth_kv WHERE key='kirocli:odic:token' COLLATE BINARY AND length(CAST(value AS BLOB))<=262144 LIMIT 1");
        var profile = Value("SELECT value FROM main.state WHERE key='api.codewhisperer.profile' COLLATE BINARY AND length(CAST(value AS BLOB))<=262144 LIMIT 1");
        transaction.Commit();
        if (token is null || profile is null) return null;
        using var a = JsonDocument.Parse(token); using var b = JsonDocument.Parse(profile);
        var access = Text(a.RootElement, "access_token"); var arn = Text(b.RootElement, "arn");
        return string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(arn) ? null : JsonSerializer.Serialize(new { access_token = access, profileArn = arn });
    }
}
