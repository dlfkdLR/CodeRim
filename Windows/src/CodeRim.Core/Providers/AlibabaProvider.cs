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
    private static ProviderReading ParseAlibaba(JsonElement root)
    {
        var reading = AlibabaCodingPlanUsage.Parse(root);
        if (reading.State == ReadingState.NeedsAuth) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        return reading;
    }
}
