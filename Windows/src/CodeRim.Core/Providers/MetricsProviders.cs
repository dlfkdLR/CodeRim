using System.Globalization;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private static readonly (string Key, string Query)[] GroqQueries = [
        ("requests", "sum(model_project_id_status_code:requests:rate5m)"),
        ("input", "sum(model_project_id:tokens_in:rate5m)"),
        ("output", "sum(model_project_id:tokens_out:rate5m)"),
        ("cache", "sum(model_project_id:prompt_cache_hits:rate5m)")];
    private static async Task<ProviderReading> FetchGroq(Func<string, string?> setting, Func<string, Task<JsonElement>> get)
    {
        var url = ManagementBase(setting("GROQ_API_URL") is { Length: > 0 } custom ? custom : "https://api.groq.com/v1");
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Groq requires HTTPS.");
        var documents = new Dictionary<string, JsonElement>();
        foreach (var (key, query) in GroqQueries)
            documents[key] = await get(url + "/metrics/prometheus/api/v1/query?query=" + Uri.EscapeDataString(query)).ConfigureAwait(false);
        return ParseGroq(documents);
    }
    private static ProviderReading ParseGroq(IReadOnlyDictionary<string, JsonElement> payloads)
    {
        double Scalar(string key)
        {
            var root = payloads.GetValueOrDefault(key);
            var rows = Get(Get(root, "data"), "result");
            if (Text(root, "status") != "success" || rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid metric response.");
            double total = 0;
            foreach (var row in rows.EnumerateArray())
            {
                var sample = Get(row, "value");
                if (sample.ValueKind != JsonValueKind.Array || sample.GetArrayLength() != 2) throw new InvalidDataException("Invalid metric sample.");
                var point = JsonSerializer.SerializeToElement(new { timestamp = sample[0], value = sample[1] });
                var value = Numeric(point, "value");
                if (!Numeric(point, "timestamp").HasValue || value is not >= 0 || !double.IsFinite(total + value.Value)) throw new InvalidDataException("Invalid metric sample.");
                total += value.Value;
            }
            return total;
        }
        var requests = Scalar("requests") * 60; var tokens = (Scalar("input") + Scalar("output")) * 60; var cache = Scalar("cache") * 60;
        if (!double.IsFinite(requests) || !double.IsFinite(tokens) || !double.IsFinite(cache)) throw new InvalidDataException("Invalid metric rate.");
        var windows = new List<LimitWindow> {
            new("requests", "Request rate · last 5 minutes", Unit: "requests/min", DisplayValue: $"{requests:N2} requests/min"),
            new("tokens", "Token rate · last 5 minutes", Unit: "tokens/min", DisplayValue: $"{tokens:N2} tokens/min") };
        if (cache > 0) windows.Add(new("cache", "Cache hit rate · last 5 minutes", Unit: "hits/min", DisplayValue: $"{cache:N2} hits/min"));
        return Metered("groq", windows, "Prometheus metrics");
    }
    private static ProviderReading ParseZed(JsonElement root)
    {
        var plan = Get(root, "plan"); var quota = Get(Get(plan, "usage"), "edit_predictions");
        var used = Count(quota, "used"); var limit = Numeric(quota, "limit") ?? Numeric(Get(quota, "limit"), "limited");
        if (used is not >= 0) throw new InvalidDataException("Missing Zed edit predictions.");
        var unlimited = Text(quota, "limit") == "unlimited";
        var period = Get(plan, "subscription_period"); var start = EpochDate(period, "started_at"); var end = EpochDate(period, "ended_at");
        var duration = start.HasValue && end > start && (end.Value - start.Value).TotalMinutes < int.MaxValue ? (int)(end.Value - start.Value).TotalMinutes : 0;
        var window = new LimitWindow("edit-predictions", "Edit predictions", limit is > 0 ? Math.Clamp(used.Value / limit.Value * 100, 0, 100) : null,
            end, duration, used, Unit: "predictions", DisplayValue: unlimited ? $"{used:N0} used · Unlimited" : limit.HasValue ? $"{used:N0} / {limit:N0} predictions" : $"{used:N0} predictions");
        return Metered("zed", [window], Text(plan, "plan_v3"));
    }
}
