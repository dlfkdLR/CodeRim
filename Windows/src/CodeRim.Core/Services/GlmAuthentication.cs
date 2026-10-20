using System.Text.Json;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Services;

public sealed record GlmCredential(string Token, string Region, string Source);
/// <summary>Borrow a plaintext tool credential together with its console region.</summary>
public static class GlmAuthentication
{
    private static readonly string[] OpenCodeIds = ["zai-coding-plan", "zai", "z-ai", "z.ai", "glm", "zhipu", "zhipuai"];
    private static readonly string[] TokenFields = ["apiKey", "api_key", "token", "key", "accessToken", "auth_token"];
    private static readonly string[] Sources = ["claude", "zcode-config", "zcode-credentials", "opencode"];
    public static GlmCredential? Read(string home)
    {
        string[] paths = [Path.Combine(home, ".claude", "settings.json"), Path.Combine(home, ".zcode", "v2", "config.json"),
            Path.Combine(home, ".zcode", "v2", "credentials.json"), Path.Combine(home, ".local", "share", "opencode", "auth.json")];
        for (var index = 0; index < paths.Length; index++)
        {
            try { if (Parse(Sources[index], GuardedFile.Read(paths[index])) is { } value) return value; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        return null;
    }
    public static string? Serialize(GlmCredential? credential) => credential is null ? null : JsonSerializer.Serialize(credential);
    public static GlmCredential? Profile(string? json)
    {
        if (json is null) return null;
        var root = Document(json);
        var token = Clean(Text(root, "Token")); var region = Text(root, "Region"); var source = Text(root, "Source");
        return token is not null && region is "global" or "bigmodel-cn" && source is "Claude Code" or "ZCode" or "OpenCode"
            ? new(token, region, source) : null;
    }
    public static GlmCredential? Parse(string source, string json)
    {
        var root = Document(json);
        switch (source)
        {
            case "claude":
                var environment = Get(root, "env");
                var key = Clean(Text(environment, "ANTHROPIC_AUTH_TOKEN")) ?? Clean(Text(environment, "ANTHROPIC_API_KEY"));
                return key is not null && Region(Text(environment, "ANTHROPIC_BASE_URL")) is { } region ? new(key, region, "Claude Code") : null;
            case "zcode-config":
                var providers = Get(root, "provider");
                if (providers.ValueKind != JsonValueKind.Object) return null;
                foreach (var provider in providers.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (!provider.Name.Contains("coding-plan", StringComparison.Ordinal) || Get(provider.Value, "enabled").ValueKind == JsonValueKind.False) continue;
                    var options = Get(provider.Value, "options"); var token = Clean(Text(options, "apiKey"));
                    if (token is null) continue;
                    var address = Get(options, "baseURL");
                    string? selected = address.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                        || address.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(address.GetString()) ? "global" : Region(Text(options, "baseURL"));
                    if (selected is not null) return new(token, selected, "ZCode");
                }
                return null;
            case "zcode-credentials":
                var access = Clean(Text(root, "oauth:zai:access_token"));
                return access is not null && !access.StartsWith("enc:v1:", StringComparison.Ordinal) ? new(access, "global", "ZCode") : null;
            case "opencode":
                foreach (var id in OpenCodeIds)
                {
                    var entry = Get(root, id); var token = entry.ValueKind == JsonValueKind.String ? Clean(entry.GetString()) : null;
                    foreach (var field in TokenFields) token ??= Clean(Text(entry, field));
                    if (token is not null) return new(token, id is "zhipu" or "zhipuai" ? "bigmodel-cn" : "global", "OpenCode");
                }
                return null;
            default: return null;
        }
    }
    public static string? Clean(string? value)
        => value?.Trim() is { Length: > 0 and <= 65536 } token && !token.Any(char.IsControl) ? token : null;
    private static string? Region(string? address)
    {
        if (address is null || address.Length > 4096 || !Uri.TryCreate(address, UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0) return null;
        var host = url.IdnHost;
        if (host.Equals("api.z.ai", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".z.ai", StringComparison.OrdinalIgnoreCase)) return "global";
        if (host.Equals("open.bigmodel.cn", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".bigmodel.cn", StringComparison.OrdinalIgnoreCase)) return "bigmodel-cn";
        return null;
    }
    private static JsonElement Document(string json)
    {
        if (json.Length > 262144) return default;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return default;
            var pending = new Stack<JsonElement>(); pending.Push(document.RootElement); var count = 0;
            while (pending.TryPop(out var item))
            {
                if (++count > 16000) return default;
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in item.EnumerateObject()) { if (!names.Add(property.Name)) return default; pending.Push(property.Value); }
                }
                else if (item.ValueKind == JsonValueKind.Array) foreach (var entry in item.EnumerateArray()) pending.Push(entry);
            }
            return document.RootElement.Clone();
        }
        catch (JsonException) { return default; }
    }
}
