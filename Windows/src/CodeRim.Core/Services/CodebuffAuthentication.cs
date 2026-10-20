using System.Text.Json;
namespace CodeRim.Core.Services;

public sealed record CodebuffCredential(string Token, bool FromAuthFile);

public static class CodebuffAuthentication
{
    public static CodebuffCredential? Resolve(string? saved, string? environment, Func<string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(readFile);
        if (Clean(saved) is { } manual) return new(manual, false);
        if (Clean(environment) is { } configured) return new(configured, false);
        return Clean(readFile()) is { } local ? new(local, true) : null;
    }
    public static string? Read(string path)
    {
        try { return Parse(GuardedFile.Read(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return null; }
    }
    public static string? Parse(string json)
    {
        if (json.Length > 262144) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!Unique(root)) return null;
            var profile = root.TryGetProperty("default", out var value) ? value : default;
            if (profile.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && !Unique(profile)) return null;
            static bool Typed(JsonElement item, string key) => item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(key, out var field)
                || field.ValueKind is JsonValueKind.String or JsonValueKind.Null;
            if (!Typed(root, "authToken") || !Typed(profile, "authToken") || !Typed(profile, "fingerprintId")
                || !Typed(profile, "email") || !Typed(profile, "name")) return null;
            static string? Token(JsonElement item) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("authToken", out var token)
                && token.ValueKind == JsonValueKind.String ? Clean(token.GetString()) : null;
            return Token(profile) ?? Token(root);
        }
        catch (JsonException) { return null; }
    }
    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => names.Add(property.Name));
    }
    private static string? Clean(string? value)
    {
        if (value is null || value.Length > 65536) return null;
        var text = value.Trim();
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\''))) text = text[1..^1].Trim();
        return text.Length > 0 && !text.Any(char.IsControl) ? text : null;
    }
}
