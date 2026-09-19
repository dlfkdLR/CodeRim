using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

public sealed partial class NativeProviders
{
    private static string NormalizedKey(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static JsonElement ChuteValue(JsonElement root, string[] keys)
    {
        if (root.ValueKind != JsonValueKind.Object) return default;
        foreach (var key in keys)
            foreach (var property in root.EnumerateObject())
                if (NormalizedKey(property.Name) == NormalizedKey(key)) return property.Value;
        return default;
    }
    private static string? ChuteText(JsonElement root, string[] keys)
    {
        var value = ChuteValue(root, keys); return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
    }
    private static double? ChuteNumber(JsonElement root, string[] keys)
    {
        var value = ChuteValue(root, keys); var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
        return double.TryParse(text?.Replace(",", "", StringComparison.Ordinal).Replace("$", "", StringComparison.Ordinal).Replace("%", "", StringComparison.Ordinal),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    }
    private static readonly string[] ChuteIdentifierKeys = ["chute_id", "chuteId", "id"];
    private static JsonElement ChuteData(JsonElement root)
    {
        var data = Get(root, "data"); if (data.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return data;
        var result = Get(root, "result"); return result.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? result : root;
    }
    private static async Task<ProviderReading> FetchChutes(Func<string, string?> setting, Func<string, Task<JsonElement>> get, CancellationToken token)
    {
        var baseUrl = ManagementBase(setting("CHUTES_API_URL") is { Length: > 0 } custom ? custom : "https://api.chutes.ai");
        if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Chutes requires HTTPS.");
        var documents = new Dictionary<string, JsonElement> { ["main"] = await get(baseUrl + "/users/me/subscription_usage").ConfigureAwait(false) };
        var primary = ParseChutes(documents);
        if (primary.Windows.Any(x => x.Id == "rolling") && primary.Windows.Any(x => x.Id == "monthly")) return primary;
        var partial = false;
        try
        {
            var quotas = await get(baseUrl + "/users/me/quotas").ConfigureAwait(false);
            var data = ChuteData(quotas); var definitions = data.ValueKind == JsonValueKind.Array ? data : Get(data, "quotas");
            var enriched = new List<JsonElement>();
            if (definitions.ValueKind == JsonValueKind.Array)
            {
                partial = definitions.GetArrayLength() > 32;
                foreach (var definition in definitions.EnumerateArray().Take(32))
                {
                    if (definition.ValueKind != JsonValueKind.Object) continue;
                    var id = ChuteText(definition, ChuteIdentifierKeys);
                    var final = definition;
                    if (id is { Length: > 0 and <= 256 })
                        try
                        {
                            var usage = ChuteData(await get(baseUrl + "/users/me/quota_usage/" + Uri.EscapeDataString(id)).ConfigureAwait(false));
                            if (usage.ValueKind == JsonValueKind.Object)
                            {
                                var merged = JsonNode.Parse(definition.GetRawText())!.AsObject();
                                foreach (var pair in JsonNode.Parse(usage.GetRawText())!.AsObject()) merged[pair.Key] = pair.Value?.DeepClone();
                                final = JsonSerializer.SerializeToElement(merged);
                            }
                        }
                        catch (ProviderRequestException error) when (error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { throw; }
                        catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
                        { token.ThrowIfCancellationRequested(); partial = true; }
                    enriched.Add(final);
                }
                documents["quotas"] = JsonSerializer.SerializeToElement(enriched);
            }
            else documents["quotas"] = quotas;
        }
        catch (ProviderRequestException error) when (error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { throw; }
        catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); partial = true; }
        var reading = ParseChutes(documents);
        return partial && reading.Windows.Count > 0 ? reading with { State = ReadingState.Partial, Message = "Some quota details could not be refreshed." } : reading;
    }
    private static ProviderReading ParseChutes(IReadOnlyDictionary<string, JsonElement> documents)
    {
        var root = documents.GetValueOrDefault("main"); var data = ChuteData(root); var windows = new List<LimitWindow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(JsonElement quota, string? defaultName = null, int defaultMinutes = 0, string? explicitId = null)
        {
            if (quota.ValueKind != JsonValueKind.Object || !seen.Add(quota.GetRawText())) return;
            var used = ChuteNumber(quota, ChuteUsedKeys); var limit = ChuteNumber(quota, ChuteLimitKeys); var remaining = ChuteNumber(quota, ChuteRemainingKeys);
            var percent = ChuteNumber(quota, ChutePercentUsedKeys); if (percent.HasValue && Math.Abs(percent.Value) < 1) percent *= 100;
            if (!percent.HasValue && ChuteNumber(quota, ChutePercentRemainingKeys) is { } fraction) percent = 100 - (Math.Abs(fraction) < 1 ? fraction * 100 : fraction);
            limit ??= used + remaining; used ??= limit - remaining;
            if (!percent.HasValue && limit is > 0 && used is >= 0) percent = used / limit * 100;
            if (percent is not >= 0) return;
            var label = ChuteText(quota, ChuteLabelKeys) ?? defaultName ?? "Quota";
            var unit = ChuteText(quota, ChuteUnitKeys) ?? "credits";
            var minutes = ChuteNumber(quota, ChuteWindowMinuteKeys) ?? ChuteNumber(quota, ChuteWindowHourKeys) * 60
                ?? ChuteNumber(quota, ChuteWindowDayKeys) * 1440 ?? ChuteNumber(quota, ChuteWindowSecondKeys) / 60;
            if (!minutes.HasValue && ChuteText(quota, ChuteWindowStringKeys) is { } duration)
            {
                var match = Pattern(duration, @"^\s*([0-9]+(?:\.[0-9]+)?)\s*(months?|mo|minutes?|min|m|hours?|hr|h|days?|d)");
                if (match.Success && TextNumber(match.Groups[1].Value) is { } number)
                    minutes = number * (match.Groups[2].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? 60
                        : match.Groups[2].Value.StartsWith("d", StringComparison.OrdinalIgnoreCase) ? 1440
                        : match.Groups[2].Value.StartsWith("mo", StringComparison.OrdinalIgnoreCase) ? 43200 : 1);
            }
            var length = minutes is > 0 and < int.MaxValue ? (int)Math.Round(minutes.Value) : defaultMinutes;
            var normalized = label.ToLowerInvariant(); var id = explicitId;
            id ??= length == 240 || normalized.Contains("rolling", StringComparison.Ordinal) || normalized.Contains("4h", StringComparison.Ordinal) || normalized.Contains("4-hour", StringComparison.Ordinal) ? "rolling"
                : length >= 40320 || normalized.Contains("month", StringComparison.Ordinal) || normalized.Contains("billing", StringComparison.Ordinal) || normalized.Contains("subscription", StringComparison.Ordinal) ? "monthly" : "quota." + windows.Count;
            if (windows.Any(x => x.Id == id)) return;
            DateTimeOffset? reset = null;
            foreach (var key in ChuteResetKeys)
            {
                var value = ChuteValue(quota, [key]);
                if (value.ValueKind == JsonValueKind.Undefined) continue;
                reset = EpochDate(JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { ["value"] = value }), "value");
                if (reset.HasValue) break;
            }
            windows.Add(new(id, label, Math.Clamp(percent.Value, 0, 100), reset, length,
                Unit: unit, DisplayValue: used.HasValue && limit.HasValue ? $"{used:N2} / {limit:N2} {unit}" : null));
        }
        foreach (var (keys, name, minutes, id) in new[] { (ChuteRollingPayloadKeys, "4-hour quota", 240, "rolling"), (ChuteMonthlyPayloadKeys, "Monthly quota", 43200, "monthly") })
        {
            var quota = ChuteValue(root, keys); if (quota.ValueKind != JsonValueKind.Object) quota = ChuteValue(data, keys);
            Add(quota, name, minutes, id);
        }
        foreach (var context in Contexts(root)) Add(context);
        foreach (var context in Contexts(documents.GetValueOrDefault("quotas"))) Add(context);
        var plan = ChuteText(root, ChutePlanKeys) ?? ChuteText(data, ChutePlanKeys)
            ?? Contexts(root).Select(x => ChuteText(x, ChutePlanKeys)).FirstOrDefault(x => x is not null);
        return Metered("chutes", windows.OrderBy(x => x.Id == "rolling" ? 0 : x.Id == "monthly" ? 1 : 2).ToArray(), plan);
    }
    private static readonly string[] ChuteRollingPayloadKeys = ["rolling", "rolling_window", "rollingWindow", "rolling_4h", "rolling4h", "four_hour", "fourHour", "four_hour_usage", "fourHourUsage", "window_4h", "window4h"];
    private static readonly string[] ChuteMonthlyPayloadKeys = ["monthly", "monthly_usage", "monthlyUsage", "subscription", "subscription_usage", "subscriptionUsage", "billing_period", "billingPeriod"];
    private static readonly string[] ChuteLabelKeys = ["label", "name", "title", "type", "quota_type", "quotaType", "period", "window", "window_name", "windowName", "chute_id", "chuteId"];
    private static readonly string[] ChuteLimitKeys = ["limit", "cap", "max", "maximum", "quota", "quota_limit", "quotaLimit", "monthly_cap", "monthlyCap", "monthly_limit", "monthlyLimit", "request_limit", "requestLimit", "token_limit", "tokenLimit", "hard_limit", "hardLimit", "total"];
    private static readonly string[] ChuteUsedKeys = ["used", "usage", "used_amount", "usedAmount", "consumed", "consumed_amount", "consumedAmount", "current", "current_usage", "currentUsage", "requests", "request_count", "requestCount", "tokens", "token_usage", "tokenUsage", "monthly_usage", "monthlyUsage"];
    private static readonly string[] ChuteRemainingKeys = ["remaining", "available", "balance", "left", "remaining_amount", "remainingAmount", "available_amount", "availableAmount"];
    private static readonly string[] ChutePercentUsedKeys = ["percent_used", "percentUsed", "usage_percent", "usagePercent", "used_percent", "usedPercent", "utilization", "utilization_percent", "utilizationPercent"];
    private static readonly string[] ChutePercentRemainingKeys = ["percent_remaining", "percentRemaining", "remaining_percent", "remainingPercent"];
    private static readonly string[] ChuteResetKeys = ["reset_at", "resetAt", "resets_at", "resetsAt", "reset_time", "resetTime", "next_reset_at", "nextResetAt", "renews_at", "renewsAt", "renewal_at", "renewalAt", "period_end", "periodEnd", "current_period_end", "currentPeriodEnd", "expires_at", "expiresAt", "window_end", "windowEnd", "end_time", "endTime"];
    private static readonly string[] ChuteUnitKeys = ["unit", "units", "currency", "quota_unit", "quotaUnit"];
    private static readonly string[] ChutePlanKeys = ["plan_name", "planName", "plan", "tier", "subscription_plan", "subscriptionPlan", "subscription_tier", "subscriptionTier"];
    private static readonly string[] ChuteWindowMinuteKeys = ["window_minutes", "windowMinutes", "period_minutes", "periodMinutes", "duration_minutes", "durationMinutes"];
    private static readonly string[] ChuteWindowHourKeys = ["window_hours", "windowHours", "period_hours", "periodHours", "duration_hours", "durationHours"];
    private static readonly string[] ChuteWindowDayKeys = ["window_days", "windowDays", "period_days", "periodDays", "duration_days", "durationDays"];
    private static readonly string[] ChuteWindowSecondKeys = ["window_seconds", "windowSeconds", "period_seconds", "periodSeconds", "duration_seconds", "durationSeconds"];
    private static readonly string[] ChuteWindowStringKeys = ["window", "period", "interval", "duration"];
}
