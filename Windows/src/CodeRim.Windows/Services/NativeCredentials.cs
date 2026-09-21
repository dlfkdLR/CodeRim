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
                case "copilot":
                    return GitHubAuthentication.ParseHosts(CopilotConnection.ScopeMarker());
                case "glm":
                    return GlmAuthentication.Serialize(GlmAuthentication.Read(home));
                case "codebuff":
                    return CodebuffAuthentication.Read(Path.Combine(home, ".config", "manicode", "credentials.json"));
                case "kiro":
                    var kiroDirectory = Environment.GetEnvironmentVariable("KIRO_DATA_DIR");
                    if (kiroDirectory is { Length: > 0 }) return KiroAuthentication.Read(Path.Combine(kiroDirectory, "data.sqlite3"));
                    foreach (var kiroRoot in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kiro-cli"),
                        Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share"), "kiro-cli") })
                        if (KiroAuthentication.Read(Path.Combine(kiroRoot, "data.sqlite3")) is { } kiroAuth) return kiroAuth;
                    return null;
                case "vertexai":
                    var googleConfig = Environment.GetEnvironmentVariable("CLOUDSDK_CONFIG") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "gcloud");
                    var googlePath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS") ?? Path.Combine(googleConfig, "application_default_credentials.json");
                    return GoogleAuthentication.Read(googlePath, googleConfig, Environment.GetEnvironmentVariable("CLOUDSDK_ACTIVE_CONFIG_NAME"));
                case "gemini-cli":
                    var npmRoots = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"), Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX") ?? "" }
                        .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim('"')));
                    return GeminiAuthentication.Read(home, npmRoots);
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
                    {
                        var values = LocalStateDatabase.Read(path, "cursorAuth/accessToken", "cursorAuth/stripeMembershipAuthId");
                        string? Value(string key) => values.TryGetValue(key, out var bytes) ? new UTF8Encoding(false, true).GetString(bytes) : null;
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
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or FormatException or DecoderFallbackException or System.Text.RegularExpressions.RegexMatchTimeoutException or SqliteException) { return null; }
    }
}
