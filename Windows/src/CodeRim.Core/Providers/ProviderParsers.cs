using System.Globalization;
using System.Text.Json;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Providers;

public static class ProviderParsers
{
    public static IReadOnlyList<LimitWindow> Codex(JsonElement root)
    {
        var result = new List<LimitWindow>();
        var buckets = Get(root, "rateLimitsByLimitId");
        if (buckets.ValueKind == JsonValueKind.Object && buckets.EnumerateObject().Any())
            foreach (var pair in buckets.EnumerateObject().OrderBy(pair => pair.Name, StringComparer.Ordinal)) AddCodexBucket(pair.Value, pair.Name, result);
        else AddCodexBucket(Get(root, "rateLimits"), "codex", result);
        result = result.OrderBy(window => AccountLimitPresentation.Name(window, "codex"), StringComparer.Ordinal)
            .ThenBy(window => window.DurationMinutes).ToList();
        var credits = Get(root, "rateLimitResetCredits");
        var count = CodexInteger(FirstPresent(credits, "availableCount", "count"));
        if (count < 0) count = 0;
        var unlimited = Get(credits, "unlimited").ValueKind == JsonValueKind.True;
        var expiry = CodexDate(FirstPresent(credits, "expiresAt", "expiry", "resetsAt"));
        if (count is not null || unlimited || expiry is not null)
            result.Add(new("rate-limit-reset-credits", "Reset credits", ResetsAt: expiry, RemainingCount: unlimited ? null : count,
                Unit: "resets", DisplayValue: unlimited ? "Unlimited resets" : count is null ? "Reset count unavailable" : null));
        return result;
    }
    private static void AddCodexBucket(JsonElement bucket, string id, List<LimitWindow> result)
    {
        var name = AccountLimitName(Text(bucket, "limitName")) ?? AccountLimitName(Text(bucket, "modelName"));
        var seen = new HashSet<(int Duration, double? Reset)>();
        foreach (var slot in new[] { "primary", "secondary" })
        {
            var window = Get(bucket, slot);
            var duration = CodexInteger(Get(window, "windowDurationMins"));
            if (duration is not (> 0 and <= int.MaxValue)) continue;
            var percent = Number(window, "usedPercent") ?? (Number(window, "remainingPercent") is { } remaining ? 100 - remaining : (double?)null);
            if (percent is null || !double.IsFinite(percent.Value)) continue;
            var resetSeconds = Number(window, "resetsAt");
            if (!seen.Add(((int)duration.Value, resetSeconds))) continue;
            result.Add(new LimitWindow(id + "." + slot, (id.Equals("codex", StringComparison.OrdinalIgnoreCase) ? "" : id + " · ") + AccountLimitPresentation.WindowLabel((int)duration.Value),
                Math.Clamp(percent.Value, 0, 100), CodexDate(Get(window, "resetsAt")), (int)duration.Value, AccountLimitName: name));
        }
    }
    private static JsonElement FirstPresent(JsonElement root, params string[] names)
    {
        foreach (var name in names) if (Get(root, name) is var value && value.ValueKind != JsonValueKind.Undefined) return value;
        return default;
    }
    private static long? CodexInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) return null;
        if (value.TryGetInt64(out var integer)) return integer;
        return value.TryGetDouble(out var number) && double.IsFinite(number) && number >= long.MinValue && number < 9223372036854775808d
            && Math.Truncate(number) == number ? (long)number : null;
    }
    private static DateTimeOffset? CodexDate(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds) || !double.IsFinite(seconds)) return null;
        try { return DateTimeOffset.UnixEpoch.AddSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    private static string? AccountLimitName(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) || System.Text.Encoding.UTF8.GetByteCount(text) > 256 || text.Any(char.IsControl) ? null : text;
    }
    public static IReadOnlyList<LimitWindow> Claude(JsonElement root)
    {
        var limits = Get(root, "rate_limits");
        var result = new List<LimitWindow>();
        foreach (var (id, name, duration) in new[] { ("five_hour", "5 hours", 300), ("seven_day", "Weekly", 10080) })
        {
            var window = Get(limits, id);
            if (Number(window, "used_percentage") is { } percent && percent is >= 0 and <= 100)
                result.Add(new LimitWindow(id, name, percent, Date(Get(window, "resets_at")), duration));
        }
        return result;
    }
    public static IReadOnlyList<LimitWindow> Copilot(JsonElement root)
    {
        var quotas = Get(root, "quota_snapshots");
        if (quotas.ValueKind != JsonValueKind.Object) return [];
        var result = new List<LimitWindow>();
        foreach (var quota in quotas.EnumerateObject().OrderBy(x => x.Name == "premium_interactions" ? 0 : 1))
        {
            var q = quota.Value;
            if (Get(q, "unlimited").ValueKind == JsonValueKind.True) continue;
            var total = Number(q, "entitlement"); var used = Number(q, "used"); var remaining = Number(q, "remaining");
            if (total == 0 || used is null && remaining is null) continue;
            var reset = Date(Get(q, "reset_date")) ?? Date(Get(root, "quota_reset_date_utc")) ?? Date(Get(root, "quota_reset_date"));
            result.Add(new LimitWindow(quota.Name, quota.Name.Replace('_', ' '),
                total > 0 ? Math.Max(0, used ?? total.Value - remaining!.Value) / total * 100 : null,
                reset, UsedCount: Count(q, "used"), RemainingCount: Count(q, "remaining"), Unit: "requests"));
        }
        return result;
    }
    public static IReadOnlyList<LimitWindow> Glm(JsonElement root)
    {
        if (Number(root, "code") is { } code && code != 200 || Get(root, "success").ValueKind == JsonValueKind.False) throw new InvalidDataException("GLM did not return a successful reading.");
        var limits = Get(Get(root, "data"), "limits");
        if (limits.ValueKind != JsonValueKind.Array) return [];
        var result = new List<LimitWindow>();
        foreach (var limit in limits.EnumerateArray())
        {
            var unit = Number(limit, "unit"); var number = Number(limit, "number");
            var type = Text(limit, "type");
            var label = type == "TIME_LIMIT" ? "MCP (1 month)" : unit == 3 ? $"{number} hours" : unit == 6 ? $"{number} weeks" : "Usage";
            var percent = Number(limit, "percentage");
            var tokens = type == "TOKENS_LIMIT" ? Count(limit, "currentValue") : null;
            if (percent is null && tokens is null) continue;
            result.Add(new LimitWindow(label, label, percent >= 0 ? percent : null,
                Date(Get(limit, "nextResetTime"), milliseconds: true),
                (int)Math.Clamp((unit == 3 ? 60 : unit == 6 ? 10080 : 0) * (number ?? 0), 0, int.MaxValue),
                UsedCount: tokens, Unit: tokens.HasValue ? "tokens" : null));
        }
        return result;
    }
    public static JsonElement Get(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var result) ? result : default;
    public static string? Text(JsonElement root, string name) => Get(root, name).ValueKind == JsonValueKind.String ? Get(root, name).GetString() : null;
    public static double? Number(JsonElement root, string name)
    {
        var item = Get(root, name);
        return item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;
    }
    public static long? Count(JsonElement root, string name) => Number(root, name) is { } number && number >= 0 && number <= 1e15 && Math.Truncate(number) == number ? (long)number : null;
    public static DateTimeOffset? Date(JsonElement value, bool milliseconds = false)
    {
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number <= 0) return null;
        try { return DateTimeOffset.UnixEpoch.AddSeconds(milliseconds || number > 10_000_000_000 ? number / 1000 : number); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
