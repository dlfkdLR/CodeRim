using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Services;

// A credential and its display identity are read from one file/database snapshot.
// This is not an authentication principal and never enters a companion snapshot.
public sealed record NativeProviderLogin([property: JsonIgnore] string Credential, ProviderAccountMetadata Account, string? Plan = null)
{
    public override string ToString() => Account.Source + " login";

    public static NativeProviderLogin? CommandCode(JsonElement root)
        => Secret(Text(root, "apiKey")) is { } key
            ? new(key, new(ProviderAccountMetadata.DisplayText(Text(root, "userName")), "Command Code")) : null;

    public static NativeProviderLogin? Grok(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var item in root.EnumerateObject())
        {
            if (!(item.Name == "https://auth.x.ai" || item.Name.StartsWith("https://auth.x.ai::", StringComparison.Ordinal)
                || Text(item.Value, "oidc_issuer") == "https://auth.x.ai")) continue;
            if (Date(Get(item.Value, "expires_at")) is { } expires && expires <= now) continue;
            if (Secret(Text(item.Value, "key")) is { } key)
                return new(key, new(ProviderAccountMetadata.DisplayText(Text(item.Value, "email")), "Grok"));
        }
        return null;
    }

    public static NativeProviderLogin? Cursor(IReadOnlyDictionary<string, byte[]> values)
    {
        string? Value(string key)
        {
            try { return values.TryGetValue(key, out var bytes) ? new UTF8Encoding(false, true).GetString(bytes) : null; }
            catch (DecoderFallbackException) { return null; }
        }
        var access = Secret(Value("cursorAuth/accessToken"));
        if (access is null) return null;
        var subject = Value("cursorAuth/stripeMembershipAuthId");
        if (string.IsNullOrEmpty(subject)) subject = Subject(access);
        if (string.IsNullOrEmpty(subject) || subject.Length > 4096 || subject.Any(char.IsWhiteSpace)
            || subject.Any(char.IsControl) || subject.Contains(';', StringComparison.Ordinal) || subject.Contains("::", StringComparison.Ordinal)) return null;
        return new("WorkosCursorSessionToken=" + subject + "::" + access,
            new(ProviderAccountMetadata.DisplayText(Value("cursorAuth/cachedEmail")), "Cursor"),
            ProviderAccountMetadata.DisplayText(Value("cursorAuth/stripeMembershipType")));
    }

    private static string? Secret(string? value) => string.IsNullOrWhiteSpace(value) || value.Length > 65536 || value.Any(char.IsControl) ? null : value;
    private static string? Subject(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
            return Text(claims.RootElement, "sub");
        }
        catch (Exception error) when (error is JsonException or FormatException) { return null; }
    }
}
