using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeRim.Core.Services;

public sealed record StepFunCredential(string Owner, string Kind, string? Token = null, string? Username = null, string? Password = null);

public static class StepFunAuthentication
{
    public static string? Mode(string? value) => value?.Trim().ToLowerInvariant() switch
    { null or "" or "auto" => "auto", "manual" => "manual", _ => null };
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string? Token(string? value)
    {
        if (value is null || value.Length > 65536) return null;
        var text = value.Trim();
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\''))) text = text[1..^1].Trim();
        if (text.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) text = text[7..].Trim();
        if (text.Contains("Oasis-Token=", StringComparison.Ordinal))
        {
            var tokens = text.Split(';').Select(x => x.Trim()).Where(x => x.StartsWith("Oasis-Token=", StringComparison.Ordinal)).ToArray();
            if (tokens.Length != 1) return null;
            text = tokens[0]["Oasis-Token=".Length..];
        }
        return text is { Length: > 0 and <= 32768 } && text.All(c => c >= 0x21 && c <= 0x7e && c is not '"' and not '\'' and not ';' and not ',' and not '\\')
            ? text : null;
    }
    public static StepFunCredential? Manual(string? raw) => Token(raw) is { } token ? new(Hash(token), "manual", token) : null;
    public static StepFunCredential? Login(string? username, string? password, string? owner = null)
        => username?.Trim() is { Length: > 0 and <= 320 } user && !user.Any(char.IsControl)
            && password is { Length: > 0 and <= 4096 } && !password.Contains('\0')
            ? new(owner ?? Hash(JsonSerializer.Serialize(new { user, password })), "login", Username: user, Password: password) : null;
    public static StepFunCredential? Saved(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!raw.TrimStart().StartsWith('{')) return Manual(raw);
        if (raw.Length > 65536) return null;
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count()) return null;
            var profile = JsonSerializer.Deserialize<StepFunCredential>(root);
            if (profile?.Owner is not { Length: 64 } || !profile.Owner.All(char.IsAsciiHexDigit)
                || profile.Token is not null && Token(profile.Token) != profile.Token) return null;
            return profile.Kind switch
            {
                "manual" when profile.Token is not null && profile.Username is null && profile.Password is null => profile,
                "login" when Login(profile.Username, profile.Password) is not null => profile,
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }
    public static string Scope(StepFunCredential profile) => Hash(JsonSerializer.Serialize(new { profile.Owner, profile.Kind, profile.Username, profile.Password }));
    public static string CombinedToken(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in root.EnumerateObject()) if (!names.Add(item.Name)) throw new InvalidDataException();
        string? Read(string key)
        {
            if (!root.TryGetProperty(key, out var pair) || pair.ValueKind == JsonValueKind.Null) return null;
            if (pair.ValueKind != JsonValueKind.Object || pair.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != pair.EnumerateObject().Count()
                || !pair.TryGetProperty("raw", out var raw) || raw.ValueKind != JsonValueKind.String) throw new InvalidDataException();
            var text = raw.GetString();
            return string.IsNullOrEmpty(text) ? null : Token(text) ?? throw new InvalidDataException();
        }
        var access = Read("accessToken") ?? throw new InvalidDataException();
        var refresh = Read("refreshToken");
        return Token(refresh is null ? access : access + "..." + refresh) ?? throw new InvalidDataException();
    }
}
