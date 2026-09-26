using System.IO;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Windows.Services;

internal static class NativeCredentials
{
    internal static NativeAccountSummary? ReadSummary(string id)
    {
        try
        {
            if (id == "copilot") return GitHubAuthentication.AccountSummary(CopilotConnection.ScopeMarker());
            if (id == "cursor")
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb");
                return NativeAccountSummary.Cursor(LocalStateDatabase.ReadWithOptionalValues(path, [], "cursorAuth/accessToken",
                    "cursorAuth/stripeMembershipAuthId", "cursorAuth/cachedEmail", "cursorAuth/stripeMembershipType"));
            }
            if (id == "grok")
            {
                using var document = JsonDocument.Parse(GuardedFile.Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "auth.json")));
                return NativeAccountSummary.Grok(document.RootElement, DateTimeOffset.Now);
            }
            return NativeAccountSummary.FromLogin(ReadAccount(id)) ?? NativeAccountSummary.FromCredential(id, Read(id), "local");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or FormatException or DecoderFallbackException or SqliteException) { return null; }
    }

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
                case "kimi":
                    return JsonSerializer.Serialize(KimiAuthentication.ReadCli(home, Environment.GetEnvironmentVariable, DateTimeOffset.UtcNow));
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
                case "commandcode":
                case "cursor":
                    return ReadAccount(id)?.Credential;
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
                default: return null;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or FormatException or DecoderFallbackException or System.Text.RegularExpressions.RegexMatchTimeoutException or SqliteException) { return null; }
    }

    internal static NativeProviderLogin? ReadAccount(string id)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            if (id == "cursor")
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb");
                return NativeProviderLogin.Cursor(LocalStateDatabase.ReadWithOptionalValues(path, ["cursorAuth/accessToken", "cursorAuth/stripeMembershipAuthId"],
                    "cursorAuth/cachedEmail", "cursorAuth/stripeMembershipType"));
            }
            if (id == "grok")
            {
                using var document = JsonDocument.Parse(GuardedFile.Read(Path.Combine(home, ".grok", "auth.json")));
                return NativeProviderLogin.Grok(document.RootElement, DateTimeOffset.Now);
            }
            if (id == "commandcode")
            {
                if (Environment.GetEnvironmentVariable("COMMAND_CODE_API_KEY") is { Length: > 0 } key)
                    return new(key, new(null, "Command Code"));
                using var document = JsonDocument.Parse(GuardedFile.Read(Path.Combine(home, ".commandcode", "auth.json")));
                return NativeProviderLogin.CommandCode(document.RootElement);
            }
            return null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or FormatException or DecoderFallbackException or SqliteException) { return null; }
    }
}
