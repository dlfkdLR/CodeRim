using System.Globalization;
using System.Text.Json;

namespace CodeRim.Core.Services;

public sealed record KimiCredential(string Source, string? Token, string? ApiBaseUrl = null,
    double? ExpiresAt = null, string? FilePath = null, string? DeviceId = null, string? BrowserState = null);

public static class KimiAuthentication
{
    private static readonly string Device = Guid.NewGuid().ToString("D");
    public static string? Source(string? value) => value?.Trim().ToLowerInvariant() switch
    { null or "" or "auto" => "auto", "api" => "api", "web" => "web", _ => null };
    public static string? Clean(string? value)
    {
        var text = value?.Trim();
        return text is { Length: > 0 and <= 32768 } && text.All(c => c >= 0x21 && c <= 0x7e && c is not '"' and not ',' and not ';' and not '\\')
            ? text : null;
    }
    public static string? ApiKey(string? value)
    {
        var text = value?.Trim();
        if (text is { Length: >= 2 } && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
            text = text[1..^1].Trim();
        return Clean(text);
    }
    public static string? WebToken(string? value)
    {
        if (value is null || value.Length > 65536) return null;
        var text = value.Trim();
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\''))) text = text[1..^1].Trim();
        if (text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Clean(text[7..]);
        if (text.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[7..].Trim();
            if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\''))) text = text[1..^1].Trim();
        }
        if (!text.Contains('=')) return Clean(text);
        var values = text.Split(';').Select(pair => pair.Trim()).Where(pair => pair.StartsWith("kimi-auth=", StringComparison.Ordinal))
            .Select(pair => Clean(pair["kimi-auth=".Length..])).ToArray();
        return values.Length == 1 ? values[0] : null;
    }
    public static bool AllowsCli(Func<string, string?> setting) => string.IsNullOrWhiteSpace(setting("KIMI_CODE_BASE_URL"))
        && string.IsNullOrWhiteSpace(setting("KIMI_CODE_OAUTH_HOST")) && string.IsNullOrWhiteSpace(setting("KIMI_OAUTH_HOST"));
    public static KimiCredential? Resolve(string? mode, Func<string?> api, Func<KimiCredential?> cli,
        Func<KimiCredential?> web, Func<string, string?> setting)
    {
        var source = Source(mode);
        if (source is null) return null;
        if (source != "web")
        {
            var raw = api();
            if (!string.IsNullOrWhiteSpace(raw) || source == "api") return new("api", ApiKey(raw), setting("KIMI_CODE_BASE_URL")?.Trim());
            if (AllowsCli(setting) && cli() is { } local) return local;
        }
        return web() ?? new("web", null);
    }
    public static KimiCredential? ReadCli(string userHome, Func<string, string?> setting, DateTimeOffset now)
    {
        if (!AllowsCli(setting)) return null;
        var home = setting("KIMI_CODE_HOME")?.Trim();
        if (string.IsNullOrEmpty(home)) home = Path.Combine(userHome, ".kimi-code");
        if (!Path.IsPathFullyQualified(home)) return null;
        var path = Path.Combine(home, "credentials", "kimi-code.json");
        try
        {
            var credential = ParseCli(GuardedFile.Read(path), now, Path.GetFullPath(path));
            if (credential is null) return null;
            string? device = null;
            try { device = Clean(GuardedFile.Read(Path.Combine(home, "device_id"), 1024)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            return credential with { DeviceId = device is { Length: <= 256 } ? device : Device };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { return null; }
    }
    public static KimiCredential? ParseCli(string json, DateTimeOffset now, string? path = null)
    {
        if (json.Length > 262144) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count()) return null;
            if (!root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String || Clean(access.GetString()) is not { } token) return null;
            if (root.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind is not JsonValueKind.String and not JsonValueKind.Null) return null;
            if (!root.TryGetProperty("expires_at", out var expiry)) return null;
            var text = expiry.ValueKind == JsonValueKind.String ? expiry.GetString() : expiry.ValueKind == JsonValueKind.Number ? expiry.GetRawText() : null;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                || !double.IsFinite(seconds) || seconds <= now.ToUnixTimeMilliseconds() / 1000.0 + 60 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return null;
            return new("cli", token, ExpiresAt: seconds, FilePath: path, DeviceId: Device);
        }
        catch (JsonException) { return null; }
    }
    public static KimiCredential? LocalProfile(string? json, DateTimeOffset now)
    {
        if (json is null || json.Length > 262144) return null;
        try
        {
            var value = JsonSerializer.Deserialize<KimiCredential>(json);
            return value is { Source: "cli", ApiBaseUrl: null, BrowserState: null, ExpiresAt: { } expiry }
                && Clean(value.Token) is not null && double.IsFinite(expiry) && expiry > now.ToUnixTimeMilliseconds() / 1000.0 + 60
                && value.DeviceId is { Length: > 0 and <= 256 } && Clean(value.DeviceId) is not null ? value : null;
        }
        catch (JsonException) { return null; }
    }
}
