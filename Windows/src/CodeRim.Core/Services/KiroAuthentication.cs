using System.Text.Json;
using Microsoft.Data.Sqlite;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Services;
public static class KiroAuthentication
{
    public static string? Read(string database)
    {
        if (!Path.IsPathFullyQualified(database) || !File.Exists(database)) return null;
        for (var path = Path.GetFullPath(database); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked credential database.");
        if (new FileInfo(database).Length > 256 * 1024 * 1024) return null;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open();
        string? Value(string sql)
        {
            using var command = connection.CreateCommand(); command.CommandText = sql;
            return command.ExecuteScalar() is string value && value.Length <= 262144 ? value : null;
        }
        var token = Value("SELECT value FROM auth_kv WHERE key='kirocli:odic:token' AND length(value)<=262144");
        var profile = Value("SELECT value FROM state WHERE key='api.codewhisperer.profile' AND length(value)<=262144");
        if (token is null || profile is null) return null;
        using var a = JsonDocument.Parse(token); using var b = JsonDocument.Parse(profile);
        var access = Text(a.RootElement, "access_token"); var arn = Text(b.RootElement, "arn");
        return string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(arn) ? null : JsonSerializer.Serialize(new { access_token = access, profileArn = arn });
    }
}
