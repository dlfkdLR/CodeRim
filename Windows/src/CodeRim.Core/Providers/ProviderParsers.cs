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
            foreach (var pair in buckets.EnumerateObject()) AddCodexBucket(pair.Value, pair.Name, result);
        else AddCodexBucket(Get(root, "rateLimits"), "codex", result);
        var credits = Get(root, "rateLimitResetCredits");
        var count = Count(credits, "availableCount") ?? Count(credits, "count");
        var unlimited = Get(credits, "unlimited").ValueKind == JsonValueKind.True;
        var expiry = Date(Get(credits, "expiresAt")) ?? Date(Get(credits, "expiry")) ?? Date(Get(credits, "resetsAt"));
        if (count is not null || unlimited || expiry is not null)
            result.Add(new("rate-limit-reset-credits", "Reset credits", ResetsAt: expiry, RemainingCount: unlimited ? null : count,
                Unit: "resets", DisplayValue: unlimited ? "Unlimited resets" : count is null ? "Reset count unavailable" : null));
        return result;
    }
    private static void AddCodexBucket(JsonElement bucket, string id, List<LimitWindow> result)
    {
        foreach (var slot in new[] { "primary", "secondary" })
        {
            var window = Get(bucket, slot);
            var percent = Number(window, "usedPercent");
            if (percent is null || percent < 0) continue;
            var duration = Number(window, "windowDurationMins");
            result.Add(new LimitWindow(id + "." + slot, (id == "codex" ? "" : id + " · ") + (duration == 300 ? "5 hours" : duration == 10080 ? "Weekly" : slot),
                percent, Date(Get(window, "resetsAt")), (int)Math.Clamp(duration ?? 0, 0, int.MaxValue)));
        }
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
