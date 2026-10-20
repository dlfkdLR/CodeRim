using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Services;

// A detected local connection is display evidence, not permission to send a
// request. In particular, expired Grok and token-less Cursor remain displayable.
public sealed record NativeAccountSummary(ProviderAccountMetadata? Account, string? Plan,
    [property: JsonIgnore] string Version)
{
    public override string ToString() => "Detected provider connection";
    public static bool Supports(string id) => id is "cursor" or "grok" or "commandcode" or "opencode" or "ollama" or "copilot" or "glm";

    public static NativeAccountSummary Create(ProviderAccountMetadata? account, string? plan, string sourceIdentity)
        => new(account, plan, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { account, plan, sourceIdentity }))));
    public static NativeAccountSummary? FromLogin(NativeProviderLogin? login)
        => login is null ? null : Create(login.Account, login.Plan, login.Credential);
    public static NativeAccountSummary? Glm(GlmCredential? credential)
        => credential is not null && GlmAuthentication.Clean(credential.Token) is { } token
            && credential.Region is "global" or "bigmodel-cn"
            && credential.Source is "Claude Code" or "ZCode" or "OpenCode" or "api"
            ? Create(new(null, credential.Source, credential.Region), null, "glm:" + token) : null;
    public static NativeAccountSummary? FromCredential(string id, string? credential, string source = "key")
    {
        if (string.IsNullOrWhiteSpace(credential)) return null;
        var name = id switch { "cursor" => "Cursor", "grok" => "Grok", "commandcode" => "Command Code", "opencode" => "OpenCode", "ollama" => "Ollama", "copilot" => "GitHub", _ => null };
        return name is null ? null : Create(new(null, name), id == "opencode" ? "Go" : null, source + ":" + credential);
    }

    public static NativeAccountSummary? Cursor(IReadOnlyDictionary<string, byte[]> values)
    {
        string? Value(string key)
        {
            try { return values.TryGetValue(key, out var bytes) ? new UTF8Encoding(false, true).GetString(bytes) : null; }
            catch (DecoderFallbackException) { return null; }
        }
        var email = ProviderAccountMetadata.DisplayText(Value("cursorAuth/cachedEmail"));
        var login = NativeProviderLogin.Cursor(values);
        if (email is null && login is null) return null;
        return Create(email is null ? null : new(email, "Cursor"),
            email is null ? null : ProviderAccountMetadata.DisplayText(Value("cursorAuth/stripeMembershipType")),
            login?.Credential ?? "cursor-display-only");
    }

    public static NativeAccountSummary? Grok(JsonElement root, DateTimeOffset now)
    {
        if (NativeProviderLogin.Grok(root, now) is { } live) return FromLogin(live);
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var item in root.EnumerateObject())
        {
            if (!(item.Name == "https://auth.x.ai" || item.Name.StartsWith("https://auth.x.ai::", StringComparison.Ordinal)
                || Text(item.Value, "oidc_issuer") == "https://auth.x.ai")) continue;
            var key = Text(item.Value, "key");
            if (string.IsNullOrWhiteSpace(key) || key.Length > 65536 || key.Any(char.IsControl)) continue;
            return Create(new(ProviderAccountMetadata.DisplayText(Text(item.Value, "email")), "Grok"), null, key);
        }
        return null;
    }
}
