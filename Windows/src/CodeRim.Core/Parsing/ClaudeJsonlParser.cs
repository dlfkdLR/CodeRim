using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Parsing;

/// Projects only accounting metadata. Prompts and response bodies never leave the parser.
public static class ClaudeJsonlParser
{
    public static UsageEvent? Parse(ReadOnlyMemory<byte> line, byte[]? projectKey = null)
    {
        if (line.Length > CodexJsonlParser.MaximumLineBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (JsonFields.Text(root, "type") != "assistant" || JsonFields.Flag(root, "isApiErrorMessage")) return null;
            var message = JsonFields.Object(root, "message");
            if (JsonFields.Text(message, "role") != "assistant") return null;
            var id = JsonFields.Text(message, "id");
            var session = JsonFields.Text(root, "sessionId");
            var model = JsonFields.Text(message, "model");
            if (id is null || session is null || model is null || !model.StartsWith("claude-", StringComparison.Ordinal)) return null;
            if (!DateTimeOffset.TryParse(JsonFields.Text(root, "timestamp"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var date)) return null;
            var usage = JsonFields.Object(message, "usage");
            var input = JsonFields.Count(usage, "input_tokens");
            var output = JsonFields.Count(usage, "output_tokens");
            var read = JsonFields.OptionalCount(usage, "cache_read_input_tokens");
            var write = JsonFields.OptionalCount(usage, "cache_creation_input_tokens");
            if (input is null || output is null || read is null || write is null) return null;
            return new UsageEvent(Hash(id), date, new TokenUsage(input.Value + read.Value + write.Value, read.Value, output.Value, write.Value),
                model.ToLowerInvariant(), JsonFields.Folder(JsonFields.Text(root, "cwd")), Hash(session), "claude", ProjectIdentity(JsonFields.Text(root, "cwd"), projectKey));
        }
        catch (JsonException) { return null; }
    }

    // Stream revisions take maxima of disjoint components, never max(total input).
    public static UsageEvent Merge(UsageEvent first, UsageEvent next)
    {
        var read = Math.Max(first.Usage.CachedInputTokens, next.Usage.CachedInputTokens);
        var write = Math.Max(first.Usage.CacheWriteInputTokens ?? 0, next.Usage.CacheWriteInputTokens ?? 0);
        var input = Math.Max(first.Usage.UncachedInputTokens, next.Usage.UncachedInputTokens);
        return first with
        {
            OccurredAt = first.OccurredAt < next.OccurredAt ? first.OccurredAt : next.OccurredAt,
            Usage = new TokenUsage(input + read + write, read, Math.Max(first.Usage.OutputTokens, next.Usage.OutputTokens), write)
        };
    }

    private static readonly byte[] ProcessProjectKey = RandomNumberGenerator.GetBytes(32);
    internal static string ProjectIdentity(string? path, byte[]? key) => path is null ? "unknown" :
        Convert.ToHexString(HMACSHA256.HashData(key ?? ProcessProjectKey, Encoding.UTF8.GetBytes(path.Replace('\\', '/').TrimEnd('/'))));
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal static class JsonFields
{
    internal static JsonElement Object(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : default;
    internal static string? Text(JsonElement parent, string name)
    {
        var value = Object(parent, name);
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return text is { Length: > 0 and <= 512 } && !text.Any(char.IsControl) ? text : null;
    }
    internal static bool Flag(JsonElement parent, string name) => Object(parent, name).ValueKind == JsonValueKind.True;
    internal static long? Count(JsonElement parent, string name)
    {
        var value = Object(parent, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            && number is >= 0 and <= CodexJsonlParser.MaximumTokenComponent ? number : null;
    }
    internal static long? OptionalCount(JsonElement parent, string name) =>
        Object(parent, name).ValueKind == JsonValueKind.Undefined ? 0 : Count(parent, name);
    internal static string Folder(string? path) => path is null ? "Unknown project" : path.Replace('\\', '/').TrimEnd('/').Split('/').Last();
}
