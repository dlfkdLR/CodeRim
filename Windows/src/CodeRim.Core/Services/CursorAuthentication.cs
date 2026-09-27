using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeRim.Core.Domain;
using Microsoft.Data.Sqlite;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Services;

public sealed record CursorLocalConnection([property: JsonIgnore] NativeProviderLogin? Login, NativeAccountSummary? Summary)
{
    public override string ToString() => "Detected Cursor connection";
}

// Borrow only the current editor/agent session. The owning application remains
// responsible for signing in and rotating it; discovery performs no HTTP/write.
public static class CursorAuthentication
{
    public static CursorLocalConnection Read(string home, string applicationData, Func<string, string?> environment, DateTimeOffset now)
    {
        NativeAccountSummary? editorSummary = null;
        try
        {
            var path = Path.Combine(applicationData, "Cursor", "User", "globalStorage", "state.vscdb");
            var values = LocalStateDatabase.ReadWithOptionalValues(path, [], "cursorAuth/accessToken",
                "cursorAuth/stripeMembershipAuthId", "cursorAuth/cachedEmail", "cursorAuth/stripeMembershipType");
            editorSummary = NativeAccountSummary.Cursor(values);
            if (NativeProviderLogin.Cursor(values) is { } editor) return new(editor, editorSummary);
        }
        catch (Exception error) when (ReadFailure(error)) { }
        var agent = ReadAgent(home, environment, now);
        // Never put an editor email beside quota fetched with an agent token.
        return agent.Login is not null ? agent : new(null, editorSummary ?? agent.Summary);
    }

    public static CursorLocalConnection ReadAgent(string home, Func<string, string?> environment, DateTimeOffset now)
    {
        if (environment("AGENT_CLI_CREDENTIAL_STORE") == "memory") return new(null, null);
        try
        {
            // These paths follow the native Windows agent credential/config
            // stores, not the macOS keychain or the editor's SQLite location.
            var appData = environment("APPDATA") is { Length: > 0 } roaming ? roaming : Path.Combine(home, "AppData", "Roaming");
            var configRoot = !string.IsNullOrWhiteSpace(environment("CURSOR_CONFIG_DIR")) ? environment("CURSOR_CONFIG_DIR")!
                : !string.IsNullOrWhiteSpace(environment("XDG_CONFIG_HOME")) ? Path.Combine(environment("XDG_CONFIG_HOME")!, "cursor")
                : Path.Combine(home, ".cursor");
            var authPath = Path.Combine(appData, "Cursor", "auth.json");
            var configPath = Path.Combine(configRoot, "cli-config.json");
            return ReadAgentFiles(authPath, configPath, now, ReadOptional);
        }
        catch (Exception error) when (ReadFailure(error)) { return new(null, null); }
    }

    internal static CursorLocalConnection ReadAgentFiles(string authPath, string configPath, DateTimeOffset now, Func<string, string?> read)
    {
        // Files are updated separately by the CLI. Reject a changing pair and
        // bind any display metadata to the token's account below.
        var auth = read(authPath); var config = read(configPath);
        if (auth != read(authPath) || config != read(configPath)) return new(null, null);
        var root = Object(auth); var info = Get(Object(config), "authInfo");
        if (!UniqueObject(info)) info = default;
        var token = Token(Text(root, "accessToken"));
        var claims = Claims(token);
        var jwtSubject = Subject(Text(claims, "sub"));
        var authId = Subject(Text(info, "authId"));
        var userIdValue = Get(info, "userId");
        var userId = Subject(userIdValue.ValueKind == JsonValueKind.String ? userIdValue.GetString()
            : userIdValue.ValueKind == JsonValueKind.Number && userIdValue.TryGetInt64(out var numeric) && numeric >= 0
                ? numeric.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        var email = ProviderAccountMetadata.DisplayText(Text(info, "email"));
        var configuredSubject = authId ?? userId;
        var conflict = jwtSubject is not null && configuredSubject is not null && jwtSubject != configuredSubject;
        var subject = conflict ? jwtSubject : configuredSubject ?? jwtSubject;
        // A numeric userId is not proof that an unrelated JWT subject owns the
        // cached email. Use the token subject without displaying that identity.
        var metadataBound = !conflict && (token is null || jwtSubject is null || configuredSubject == jwtSubject);
        ProviderAccountMetadata? account = metadataBound && (email is not null || authId is not null)
            ? new(email, "cursor-agent") : null;
        var expiration = Get(claims, "exp");
        var expired = expiration.ValueKind == JsonValueKind.Number && expiration.TryGetDouble(out var seconds)
            && (!double.IsFinite(seconds) || seconds <= now.ToUnixTimeMilliseconds() / 1000d);
        var login = token is not null && subject is not null && !expired
            ? new NativeProviderLogin("WorkosCursorSessionToken=" + subject + "::" + token, account ?? new(null, "cursor-agent")) : null;
        if (account is null && login is null) return new(null, null);
        var summary = NativeAccountSummary.Create(account, null, JsonSerializer.Serialize(new
        {
            source = "cursor-agent", authPath, configPath, token, subject, authId, userId, expired
        }));
        return new(login, summary);
    }

    private static string? ReadOptional(string path)
    {
        try
        {
            if (!IsLocalLoginPath(path)) return null;
            return GuardedFile.ReadUtf8(path);
        }
        catch (Exception error) when (ReadFailure(error)) { return null; }
    }
    // Pure preflight: Win32 normalizes forward/mixed slashes before resolving
    // UNC/device paths. No attribute query or other I/O may precede this guard.
    internal static bool IsLocalLoginPath(string path) => Path.IsPathFullyQualified(path)
        && !path.Replace('/', '\\').StartsWith(@"\\", StringComparison.Ordinal);
    private static bool ReadFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or DecoderFallbackException or ArgumentException or SqliteException or System.Security.SecurityException;
    private static JsonElement Object(string? json)
    {
        if (json is null || json.Length > 262144) return default;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            return UniqueObject(document.RootElement) ? document.RootElement.Clone() : default;
        }
        catch (JsonException) { return default; }
    }
    private static bool UniqueObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => names.Add(property.Name));
    }
    private static string? Token(string? value)
    {
        if (value is null || value.Length > 65536) return null;
        var token = value.Trim();
        return token.Length > 0 && token.All(CookieCharacter) ? token : null;
    }
    private static string? Subject(string? value) => string.IsNullOrEmpty(value) || value.Length > 4096
        || !value.All(CookieCharacter)
        || value.Contains("::", StringComparison.Ordinal) ? null : value;
    private static bool CookieCharacter(char value) => value is >= '\x21' and <= '\x7e' && value is not ('"' or ',' or ';' or '\\');
    private static JsonElement Claims(string? token)
    {
        if (token is null) return default;
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return default;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return Object(new UTF8Encoding(false, true).GetString(Convert.FromBase64String(payload)));
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException) { return default; }
    }
}
