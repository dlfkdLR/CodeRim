using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private static void ConfigureDoubao(HttpRequestMessage request, string credential, Func<string, string?> setting)
    {
        var key = setting("VOLCENGINE_ACCESS_KEY_ID") ?? setting("VOLCENGINE_ACCESS_KEY") ?? setting("VOLC_ACCESSKEY") ?? setting("DOUBAO_ACCESS_KEY_ID") ?? "";
        var region = setting("VOLCENGINE_REGION") ?? setting("VOLCENGINE_REGION_ID") ?? setting("VOLC_REGION") ?? setting("DOUBAO_REGION") ?? "cn-beijing";
        request.Method = HttpMethod.Post; request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType = new("application/x-www-form-urlencoded") { CharSet = "utf-8" };
        CloudSignature.Sign(request, [], key, credential, region, "ark", volcengine: true);
    }
    private static async Task<ProviderReading> FetchDoubao(Func<string, Task<JsonElement>> get, CancellationToken token)
    {
        var documents = new Dictionary<string, JsonElement> { ["main"] = await get("https://open.volcengineapi.com/?Action=GetCodingPlanUsage&Version=2024-01-01").ConfigureAwait(false) };
        var partial = false;
        try
        {
            var agentResponse = await get("https://open.volcengineapi.com/?Action=GetAFPUsage&Version=2024-01-01").ConfigureAwait(false);
            DoubaoAgentResult(agentResponse); documents["agent"] = agentResponse;
        }
        catch (ProviderRequestException error) when (error.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) { }
        catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); partial = true; }
        var reading = ParseDoubao(documents);
        return partial ? reading with { State = reading.Windows.Count > 0 ? ReadingState.Partial : ReadingState.Error, Message = "Agent Plan usage could not be refreshed." } : reading;
    }
    private static JsonElement DoubaoResult(JsonElement response)
    {
        var result = Get(response, "Result"); var error = Get(Get(response, "ResponseMetadata"), "Error");
        if (result.ValueKind != JsonValueKind.Object || error.ValueKind == JsonValueKind.Object) throw new InvalidDataException("Invalid Doubao plan response.");
        return result;
    }
    private static JsonElement DoubaoAgentResult(JsonElement response)
    {
        var result = DoubaoResult(response);
        foreach (var key in new[] { "AFPFiveHour", "AFPWeekly", "AFPMonthly" })
        {
            var row = Get(result, key); if (row.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
            if (row.ValueKind != JsonValueKind.Object || Numeric(row, "Used") is not >= 0 || Numeric(row, "Quota") is not >= 0)
                throw new InvalidDataException("Incomplete Agent Plan quota.");
        }
        return result;
    }
    private static ProviderReading ParseDoubao(IReadOnlyDictionary<string, JsonElement> documents)
    {
        var response = documents.GetValueOrDefault("main"); var result = DoubaoResult(response);
        if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Missing Doubao plan.");
        var windows = new List<LimitWindow>(); var quotas = Get(result, "QuotaUsage");
        if (quotas.ValueKind == JsonValueKind.Array)
            foreach (var row in quotas.EnumerateArray())
            {
                var level = Text(row, "Level"); var percent = Numeric(row, "Percent");
                if (level is not { Length: > 0 and <= 128 } || percent is not >= 0) throw new InvalidDataException("Invalid Doubao quota.");
                var minutes = level.ToLowerInvariant() switch { "session" or "5-hour" or "five_hour" or "5h" => 300, "weekly" or "week" => 10080, "monthly" or "month" => 43200, _ => 0 };
                windows.Add(new(level, level, Math.Clamp(percent.Value, 0, 100), EpochDate(row, "ResetTimestamp"), minutes));
            }
        var codingCount = windows.Count;
        var agent = documents.TryGetValue("agent", out var agentResponse) ? DoubaoAgentResult(agentResponse) : default;
        foreach (var (key, label, minutes) in new[] { ("AFPFiveHour", "Agent 5-hour", 300), ("AFPWeekly", "Agent weekly", 10080), ("AFPMonthly", "Agent monthly", 43200) })
        {
            var row = Get(agent, key); var used = Numeric(row, "Used"); var max = Numeric(row, "Quota");
            if (used is >= 0 && max is > 0) windows.Add(new(key, label, Math.Clamp(used.Value / max.Value * 100, 0, 100), EpochDate(row, "ResetTime"), minutes, Unit: "AFP", DisplayValue: $"{used:N2} / {max:N2} AFP"));
        }
        var state = codingCount == 0 && windows.Count > 0 ? "Agent Plan" : Text(result, "Status");
        if (windows.Count == 0 && state is not null) return new("doubao", ReadingState.Ready, [], DateTimeOffset.UtcNow, "No plan allowance counters were returned.", state);
        return Metered("doubao", windows, state);
    }
}
