using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Services;

/// <summary>Selection metadata only. The official CLI independently verifies a switched login.</summary>
public sealed record LoginIdentity(string Id, string Email, string Organization, string? Plan)
{
    public static LoginIdentity Codex(string json)
    {
        using var document = Parse(json);
        var root = document.RootElement; var tokens = Get(root, "tokens");
        if (Get(root, "OPENAI_API_KEY").ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            || Text(root, "auth_mode") is { } mode && mode != "chatgpt") throw Invalid();
        Token(Text(tokens, "access_token")); Token(Text(tokens, "refresh_token"));
        var encoded = Token(Text(tokens, "id_token")).Split('.');
        if (encoded.Length != 3) throw Invalid();
        var payload = encoded[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var claims = Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        var email = Label(Text(claims.RootElement, "email")); var subject = Label(Text(claims.RootElement, "sub"));
        var organization = Label(Text(tokens, "account_id")); var auth = Get(claims.RootElement, "https://api.openai.com/auth");
        if (Text(auth, "chatgpt_account_id") is { } claimed && claimed != organization) throw Invalid();
        return new(Key(organization, subject), email, organization, Text(auth, "chatgpt_plan_type"));
    }
    public static LoginIdentity Claude(string credential, string profile)
    {
        using var oauthDocument = Parse(credential); using var profileDocument = Parse(profile);
        var oauth = Get(oauthDocument.RootElement, "claudeAiOauth"); var account = Get(profileDocument.RootElement, "oauthAccount");
        Token(Text(oauth, "accessToken")); Token(Text(oauth, "refreshToken"));
        if (Number(oauth, "expiresAt") is not > 0 || Get(oauth, "scopes").ValueKind != JsonValueKind.Array
            || !Get(oauth, "scopes").EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "user:inference")) throw Invalid();
        var email = Label(Text(account, "emailAddress")); var org = Label(Text(account, "organizationUuid")); var id = Label(Text(account, "accountUuid"));
        return new(Key(org, id), email, org, Text(oauth, "subscriptionType"));
    }
    public static void ValidateCodexPolicy(JsonElement config, string organization)
    {
        if (config.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Codex configuration could not be verified.");
        var storage = Get(config, "cli_auth_credentials_store");
        if (storage.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) && (storage.ValueKind != JsonValueKind.String || storage.GetString() != "file"))
            throw new InvalidOperationException("Account switching requires Codex file-based authentication.");
        var method = Text(config, "forced_login_method");
        if (method is not null && method != "chatgpt") throw new InvalidOperationException("This Codex authentication method is managed.");
        var workspace = Get(config, "forced_chatgpt_workspace_id");
        if (workspace.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && !(workspace.ValueKind == JsonValueKind.String && workspace.GetString() == organization)
            && !(workspace.ValueKind == JsonValueKind.Array && workspace.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == organization)))
            throw new InvalidOperationException("This account is outside the managed workspace policy.");
    }
    public static string? CurrentClaudeScope()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return Claude(GuardedFile.Read(Path.Combine(config ?? Path.Combine(home, ".claude"), ".credentials.json")),
                GuardedFile.Read(Path.Combine(config ?? home, ".claude.json"))).Id;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException) { return null; }
    }
    private static JsonDocument Parse(string json) => json.Length <= 262144 ? JsonDocument.Parse(json) : throw Invalid();
    private static string Token(string? value) => value is { Length: > 0 and <= 65536 } && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl) ? value : throw Invalid();
    private static string Label(string? value) => value is { Length: > 0 and <= 320 } && !value.Any(char.IsControl) ? value : throw Invalid();
    private static string Key(string org, string subject) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(org.Length + ":" + org + subject)));
    private static InvalidDataException Invalid() => new("A complete subscription login is required. Sign in through the official CLI first.");
}
