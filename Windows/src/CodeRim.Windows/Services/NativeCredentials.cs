using System.IO;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Windows.Services;

internal static class NativeCredentials
{
    internal static string? Read(string id)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            switch (id)
            {
                case "grok":
                    using (var document = JsonDocument.Parse(GuardedFile.Read(Path.Combine(home, ".grok", "auth.json"))))
                    {
                        var root = document.RootElement;
                        if (root.ValueKind != JsonValueKind.Object) return null;
                        foreach (var item in root.EnumerateObject())
                        {
                            if (!(item.Name == "https://auth.x.ai" || item.Name.StartsWith("https://auth.x.ai::", StringComparison.Ordinal)
                                || Text(item.Value, "oidc_issuer") == "https://auth.x.ai")) continue;
                            if (Date(Get(item.Value, "expires_at")) is { } expires && expires <= DateTimeOffset.Now) continue;
                            if (Text(item.Value, "key") is { Length: > 0 } key) return key;
                        }
                    }
                    return null;
                case "commandcode":
                    if (Environment.GetEnvironmentVariable("COMMAND_CODE_API_KEY") is { Length: > 0 } commandKey) return commandKey;
                    using (var document = JsonDocument.Parse(GuardedFile.Read(Path.Combine(home, ".commandcode", "auth.json")))) return Text(document.RootElement, "apiKey");
                case "kilo":
                    using (var document = JsonDocument.Parse(GuardedFile.Read(Path.Combine(home, ".local", "share", "kilo", "auth.json"))))
                        return Text(Get(document.RootElement, "kilo"), "access");
                case "opencode":
                    var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share");
                    using (var document = JsonDocument.Parse(GuardedFile.Read(Path.Combine(data, "opencode", "auth.json"))))
                    {
                        var entry = Get(document.RootElement, "opencode-go");
                        if (entry.ValueKind == JsonValueKind.String) return entry.GetString();
                        foreach (var key in new[] { "key", "apiKey", "api_key", "token", "accessToken" }) if (Text(entry, key) is { Length: > 0 } token) return token;
                    }
                    return null;
                case "cursor":
                    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb");
                    if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
                    using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString()))
                    {
                        connection.Open();
                        string? Value(string key)
                        {
                            using var command = connection.CreateCommand(); command.CommandText = "SELECT value FROM ItemTable WHERE key=$key"; command.Parameters.AddWithValue("$key", key);
                            return command.ExecuteScalar() as string;
                        }
                        var access = Value("cursorAuth/accessToken"); var subject = Value("cursorAuth/stripeMembershipAuthId");
                        if (string.IsNullOrEmpty(access) || access.Length > 65536) return null;
                        if (string.IsNullOrEmpty(subject))
                        {
                            var parts = access.Split('.');
                            if (parts.Length < 2) return null;
                            var payload = parts[1].Replace('-', '+').Replace('_', '/'); payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                            using var claims = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))); subject = Text(claims.RootElement, "sub");
                        }
                        return string.IsNullOrEmpty(subject) ? null : "WorkosCursorSessionToken=" + subject + "::" + access;
                    }
                default: return null;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or FormatException or SqliteException) { return null; }
    }
}
