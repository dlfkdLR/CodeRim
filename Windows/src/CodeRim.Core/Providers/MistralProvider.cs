using System.Globalization;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private static string? MistralCsrf(string cookie) => cookie.Split(';').Select(x => x.Trim().Split('=', 2))
        .LastOrDefault(x => x.Length == 2 && x[0] == "csrftoken" && x[1].Length > 0 && !x[1].Any(c => char.IsControl(c) || c == ','))?[1];
    private static async Task<ProviderReading> FetchMistral(string cookie, Func<string, Task<JsonElement>> get, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var documents = new Dictionary<string, JsonElement> {
            ["main"] = await get($"https://admin.mistral.ai/api/billing/v2/usage?month={now.Month}&year={now.Year}").ConfigureAwait(false) };
        var partial = false;
        foreach (var (key, path) in new[] {
            ("credits", "https://admin.mistral.ai/api/billing/credits"),
            ("vibe", "https://console.mistral.ai/api-ui/trpc/billing.vibeUsage?batch=1&input=%7B%220%22%3A%7B%22json%22%3Anull%2C%22meta%22%3A%7B%22values%22%3A%5B%22undefined%22%5D%2C%22v%22%3A1%7D%7D%7D") })
        {
            if (key == "vibe" && MistralCsrf(NormalizeBrowserCredential("mistral", cookie)) is null) continue;
            try { documents[key] = await get(path).ConfigureAwait(false); }
            catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
            { token.ThrowIfCancellationRequested(); partial = true; }
        }
        var reading = ParseMistral(documents);
        return partial && reading.State == ReadingState.Ready ? reading with { State = ReadingState.Partial, Message = "Some billing details could not be refreshed." } : reading;
    }
    private static ProviderReading ParseMistral(IReadOnlyDictionary<string, JsonElement> documents)
    {
        var root = documents.GetValueOrDefault("main"); var windows = new List<LimitWindow>();
        var currency = Text(root, "currency")?.Trim().ToUpperInvariant();
        var prices = new Dictionary<string, double>(StringComparer.Ordinal);
        var priceRows = Get(root, "prices");
        if (priceRows.ValueKind == JsonValueKind.Array)
            foreach (var price in priceRows.EnumerateArray())
                if (Text(price, "billing_metric") is { } metric && Text(price, "billing_group") is { } group && Numeric(price, "price") is { } value)
                    prices[metric + "::" + group] = value;
        var rows = new List<ProviderCostEntry>(); double cost = 0; long input = 0, cached = 0, output = 0;
        var completeCost = true; var completeHistory = true; var foundBilling = false;
        void Models(JsonElement models, bool tokens)
        {
            if (models.ValueKind != JsonValueKind.Object) return;
            foundBilling = true;
            foreach (var model in models.EnumerateObject())
                foreach (var lane in new[] { "input", "cached", "output" })
                {
                    var entries = Get(model.Value, lane); if (entries.ValueKind != JsonValueKind.Array) continue;
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var units = Numeric(entry, "value_paid") ?? Numeric(entry, "value") ?? 0;
                        if (Math.Truncate(units) != units || units is < 0 or >= long.MaxValue) throw new InvalidDataException("Invalid billing units.");
                        var amount = (long)units;
                        if (tokens)
                        {
                            checked { if (lane == "input") input += amount; else if (lane == "cached") cached += amount; else output += amount; }
                        }
                        var key = Text(entry, "billing_metric") + "::" + Text(entry, "billing_group");
                        double? charge = prices.TryGetValue(key, out var rate) ? units * rate : units == 0 ? 0 : null;
                        if (charge.HasValue && (!double.IsFinite(charge.Value) || charge < 0)) charge = null;
                        if (!charge.HasValue) completeCost = false;
                        else { cost += charge.Value; if (!double.IsFinite(cost)) throw new InvalidDataException("Invalid billing total."); }
                        var date = EpochDate(entry, "timestamp")?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        if (date is null) { completeHistory = false; continue; }
                        if (rows.Count >= 10000) { completeHistory = false; continue; }
                        rows.Add(new(date, model.Name, tokens && lane is "input" or "cached" ? amount : null,
                            tokens && lane == "output" ? amount : null, null, null, charge, null));
                    }
                }
        }
        foreach (var category in new[] { "completion", "ocr", "connectors", "audio" }) Models(Get(Get(root, category), "models"), category == "completion");
        var libraries = Get(root, "libraries_api"); Models(Get(Get(libraries, "pages"), "models"), false); Models(Get(Get(libraries, "tokens"), "models"), true);
        var tuning = Get(root, "fine_tuning"); Models(Get(tuning, "training"), false); Models(Get(tuning, "storage"), false);
        var vibe = documents.GetValueOrDefault("vibe");
        if (vibe.ValueKind == JsonValueKind.Array && vibe.GetArrayLength() > 0)
        {
            var quota = Get(Get(Get(vibe[0], "result"), "data"), "json");
            if (Numeric(quota, "usagePercentage") is >= 0 and <= 100 and var percent)
                windows.Add(new("vibe", "Vibe monthly", percent, EpochDate(quota, "resetAt")));
        }
        if (foundBilling)
        {
            if (completeCost && currency is { Length: > 0 and <= 16 }) windows.Add(new("spend", "API spend · this month", Unit: currency, DisplayValue: $"{cost:N4} {currency}"));
            if (input > 0 || cached > 0 || output > 0)
            {
                windows.Add(new("input", "Input · this month", UsedCount: input, Unit: "tokens"));
                windows.Add(new("cached", "Cached input · this month", UsedCount: cached, Unit: "tokens"));
                windows.Add(new("output", "Output · this month", UsedCount: output, Unit: "tokens"));
            }
        }
        var credits = documents.GetValueOrDefault("credits");
        if (Numeric(credits, "wallet_amount") is { } wallet && Text(credits, "currency") is { Length: > 0 and <= 16 } creditCurrency)
        {
            var available = wallet + (Numeric(credits, "credit_notes_amount") ?? 0) - (Numeric(credits, "ongoing_usage_balance") ?? 0);
            if (double.IsFinite(available)) windows.Add(new("balance", "Available credit", Unit: creditCurrency.ToUpperInvariant(), DisplayValue: $"{Math.Max(0, available):N2} {creditCurrency.ToUpperInvariant()}"));
        }
        var reading = Metered("mistral", windows);
        if (!completeCost && windows.Count > 0) reading = reading with { State = ReadingState.Partial, Message = "Some usage has no billing price." };
        var start = EpochDate(root, "start_date"); var end = EpochDate(root, "end_date");
        if (completeHistory && start.HasValue && end >= start && currency is { Length: > 0 and <= 16 } && rows.Count > 0)
        {
            var last = end > DateTimeOffset.UtcNow ? DateTimeOffset.UtcNow : end!.Value;
            var days = Math.Clamp((int)(last.Date - start.Value.Date).TotalDays + 1, 1, 366);
            var history = new ProviderCostUsage(currency, days, "This month", last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), rows);
            history.Validate(); reading = reading with { CostUsage = history };
        }
        return reading;
    }
}
