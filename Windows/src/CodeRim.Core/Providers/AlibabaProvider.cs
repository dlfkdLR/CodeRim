using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private static (string Host, string Region, string Commodity) AlibabaRegion(Func<string, string?> setting) =>
        (setting("ALIBABA_CODING_PLAN_REGION") ?? "intl").Trim().ToLowerInvariant() switch
        {
            "intl" => ("https://modelstudio.console.alibabacloud.com", "ap-southeast-1", "sfm_codingplan_public_intl"),
            "cn" => ("https://bailian.console.aliyun.com", "cn-beijing", "sfm_codingplan_public_cn"),
            _ => throw new InvalidDataException("Alibaba region must be intl or cn.")
        };
    private static JsonElement AlibabaDecoded(JsonElement value)
    {
        for (var depth = 0; depth < 8 && value.ValueKind == JsonValueKind.String; depth++)
        {
            var text = value.GetString()?.Trim(); if (text is not { Length: > 1 } || text[0] is not ('{' or '[')) break;
            try { using var parsed = JsonDocument.Parse(text); value = parsed.RootElement.Clone(); }
            catch (JsonException) { break; }
        }
        return value;
    }
    private static async Task<ProviderReading> FetchAlibaba(Func<string, string?> setting, Func<string, Task<JsonElement>> get)
    {
        var region = AlibabaRegion(setting);
        async Task<ProviderReading> Fetch((string Host, string Region, string Commodity) selected) =>
            ParseAlibaba(await get(selected.Host + "/data/api.json?action=zeldaEasy.broadscope-bailian.codingPlan.queryCodingPlanInstanceInfoV2&product=broadscope-bailian&api=queryCodingPlanInstanceInfoV2&currentRegionId=" + selected.Region).ConfigureAwait(false));
        try { return await Fetch(region).ConfigureAwait(false); }
        catch (ProviderRequestException error) when (string.IsNullOrWhiteSpace(setting("ALIBABA_CODING_PLAN_REGION")) && error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { return await Fetch(AlibabaRegion(_ => "cn")).ConfigureAwait(false); }
    }
    private static IEnumerable<JsonElement> ExpandedContexts(JsonElement root, int depth = 0)
    {
        if (depth > 12) yield break;
        if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
            foreach (var property in root.EnumerateObject())
                foreach (var nested in ExpandedContexts(property.Value, depth + 1)) yield return nested;
        }
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray())
                foreach (var nested in ExpandedContexts(item, depth + 1)) yield return nested;
        else if (root.ValueKind == JsonValueKind.String && root.GetString()?.Trim() is { Length: > 1 } text && (text[0] == '{' || text[0] == '['))
        {
            JsonElement parsed;
            try { using var document = JsonDocument.Parse(text); parsed = document.RootElement.Clone(); }
            catch (JsonException) { yield break; }
            foreach (var nested in ExpandedContexts(parsed, depth + 1)) yield return nested;
        }
    }
    private static readonly string[] AlibabaPlanKeys = ["planName", "plan_name", "packageName", "package_name"];
    private static int AlibabaActive(JsonElement value)
    {
        var status = (Text(value, "status") ?? Text(value, "instanceStatus"))?.ToUpperInvariant();
        if (status is "ACTIVE" or "VALID") return 3;
        if (status is "EXPIRED" or "INVALID" or "INACTIVE" or "DISABLED" or "TERMINATED" or "STOPPED") return -1;
        var active = Get(value, "isActive"); if (active.ValueKind == JsonValueKind.Undefined) active = Get(value, "active");
        if (active.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            var raw = active.ValueKind == JsonValueKind.String ? active.GetString()?.Trim().ToLowerInvariant() : active.GetRawText();
            if (raw is "true" or "1" or "yes") return 3;
            if (raw is "false" or "0" or "no") return -1;
        }
        if (active.ValueKind == JsonValueKind.True) return 3;
        if (active.ValueKind == JsonValueKind.False) return -1;
        return (EpochDate(value, "endTime") ?? EpochDate(value, "periodEndTime") ?? EpochDate(value, "expireTime") ?? EpochDate(value, "expirationTime")) > DateTimeOffset.UtcNow ? 1 : 0;
    }
    private static ProviderReading ParseAlibaba(JsonElement root)
    {
        var contexts = ExpandedContexts(root).ToArray();
        foreach (var context in contexts)
        {
            var code = Numeric(context, "statusCode") ?? Numeric(context, "status_code") ?? Numeric(context, "code");
            var message = (Text(context, "statusMessage") ?? Text(context, "status_msg") ?? Text(context, "message") ?? Text(context, "msg") ?? Text(context, "code") ?? "").ToLowerInvariant();
            if (code is 401 or 403 || message.Contains("login", StringComparison.Ordinal) || message.Contains("log in", StringComparison.Ordinal)
                || message.Contains("unauthorized", StringComparison.Ordinal) || message.Contains("console session", StringComparison.Ordinal))
                throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            if (code.HasValue && code is not 0 and not 200 && message.Contains("api key", StringComparison.Ordinal)) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            if (code.HasValue && code is not 0 and not 200) throw new InvalidDataException("Alibaba rejected the quota request.");
        }
        var instances = contexts.Select(x => AlibabaDecoded(Get(x, "codingPlanInstanceInfos")).ValueKind == JsonValueKind.Array ? AlibabaDecoded(Get(x, "codingPlanInstanceInfos")) : AlibabaDecoded(Get(x, "coding_plan_instance_infos"))).FirstOrDefault(x => x.ValueKind == JsonValueKind.Array);
        var selected = instances.ValueKind == JsonValueKind.Array ? instances.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).OrderByDescending(AlibabaActive).FirstOrDefault() : default;
        var sources = selected.ValueKind == JsonValueKind.Object ? ExpandedContexts(selected).ToArray() : contexts;
        bool Quota(JsonElement x) => x.EnumerateObject().Any(p => p.Name.StartsWith("per", StringComparison.Ordinal) && (p.Name.EndsWith("UsedQuota", StringComparison.Ordinal) || p.Name.EndsWith("TotalQuota", StringComparison.Ordinal)));
        var quota = sources.FirstOrDefault(Quota);
        // An active selected instance must never borrow another subscription's quota.
        if (quota.ValueKind == JsonValueKind.Undefined && !(instances.ValueKind == JsonValueKind.Array && instances.GetArrayLength() > 1 && AlibabaActive(selected) > 0)) quota = contexts.FirstOrDefault(Quota);
        var plan = sources.Select(x => FirstText(x, AlibabaPlanKeys)).FirstOrDefault(x => x is not null);
        var windows = new List<LimitWindow>();
        void Add(string id, string label, int minutes, string prefix, string? alias = null)
        {
            var used = Numeric(quota, prefix + "UsedQuota") ?? (alias is null ? null : Numeric(quota, alias + "UsedQuota"));
            var limit = Numeric(quota, prefix + "TotalQuota") ?? (alias is null ? null : Numeric(quota, alias + "TotalQuota"));
            var reset = EpochDate(quota, prefix + "QuotaNextRefreshTime") ?? (alias is null ? null : EpochDate(quota, alias + "QuotaNextRefreshTime"));
            if (used is not >= 0 || limit is not > 0) return;
            windows.Add(new(id, label, Math.Clamp(used.Value / limit.Value * 100, 0, 100), reset, minutes,
                Unit: "requests", DisplayValue: $"{used:N0} / {limit:N0} requests"));
        }
        Add("five-hour", "5-hour limit", 300, "per5Hour", "perFiveHour"); Add("weekly", "Weekly limit", 10080, "perWeek"); Add("monthly", "Monthly limit", 0, "perBillMonth", "perMonth");
        var reading = Metered("alibaba", windows, plan);
        return windows.Count == 0 && AlibabaActive(selected) > 0 ? reading with { Message = "The plan is active, but no quota counters were returned." } : reading;
    }
}
