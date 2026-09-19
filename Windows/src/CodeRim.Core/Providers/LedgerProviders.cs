using System.Globalization;
using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

public sealed partial class NativeProviders
{
    private static readonly HashSet<string> LedgerIds = new(StringComparer.Ordinal) { "aiand", "longcat" };
    private static JsonElement LongCatData(JsonElement root)
    {
        var code = Numeric(root, "code");
        if (code is 401 or 403) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        if (code.HasValue && code is not (0 or 200)) throw new InvalidDataException();
        var data = Get(root, "data"); return data.ValueKind == JsonValueKind.Undefined ? root : data;
    }
    private static async Task<ProviderReading> FetchLedger(string id, Func<string, Task<JsonElement>> get, CancellationToken token)
    {
        var payloads = new Dictionary<string, JsonElement>(); var partial = false;
        if (id == "aiand")
        {
            const string endpoint = "https://api.aiand.com/logs?range=30days&limit=100";
            var next = endpoint; var seen = new HashSet<string>(StringComparer.Ordinal); var complete = false;
            for (var index = 0; index < 10 && seen.Add(next); index++)
            {
                JsonElement page;
                try { page = await get(next).ConfigureAwait(false); }
                catch (Exception error) when (payloads.Count > 0 && error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
                { token.ThrowIfCancellationRequested(); break; }
                if (Get(page, "data").ValueKind != JsonValueKind.Array) { if (index == 0) throw new InvalidDataException(); break; }
                payloads[index.ToString(CultureInfo.InvariantCulture)] = page;
                if (Get(page, "has_more").ValueKind != JsonValueKind.True) { complete = true; break; }
                if (Text(page, "next_after") is not { Length: > 0 and <= 1024 } after || Text(page, "next_after_id") is not { Length: > 0 and <= 1024 } afterId) break;
                next = endpoint + "&after=" + Uri.EscapeDataString(after) + "&after_id=" + Uri.EscapeDataString(afterId);
            }
            var reading = ParseLedger(id, payloads);
            return !complete && reading.Windows.Count > 0 ? reading with { State = ReadingState.Partial, Message = "The total covers the newest available request pages in the last 30 days." } : reading;
        }
        payloads["account"] = LongCatData(await get("https://longcat.chat/api/v1/user-current").ConfigureAwait(false));
        if (payloads["account"].ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        try { payloads["packs"] = LongCatData(await get("https://longcat.chat/api/pay/quota/metering/token-packs/summary").ConfigureAwait(false)); }
        catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); partial = true; }
        var lot = Get(payloads.GetValueOrDefault("packs"), "currentLot");
        if (!(Text(lot, "status")?.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) == true && Numeric(lot, "totalToken") is > 0))
            payloads["usage"] = LongCatData(await get("https://longcat.chat/api/lc-platform/v1/tokenUsage").ConfigureAwait(false));
        try { payloads["fuel"] = LongCatData(await get("https://longcat.chat/api/lc-platform/v1/pending-fuel-packages").ConfigureAwait(false)); }
        catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); partial = true; }
        var result = ParseLedger(id, payloads);
        return partial && result.Windows.Count > 0 ? result with { State = ReadingState.Partial, Message = "Primary token quota is current. Supplemental packs could not be refreshed." } : result;
    }
    private static ProviderReading ParseLedger(string id, IReadOnlyDictionary<string, JsonElement> payloads)
    {
        var windows = new List<LimitWindow>();
        if (id == "aiand")
        {
            decimal total = 0; string? currency = null;
            foreach (var page in payloads.Values)
            {
                var rows = Get(page, "data"); if (rows.ValueKind != JsonValueKind.Array) continue;
                foreach (var row in rows.EnumerateArray())
                {
                    if (!decimal.TryParse(Text(row, "cost"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cost)
                        || Text(row, "currency")?.Trim().ToUpperInvariant() is not { Length: > 0 and <= 16 } code) continue;
                    currency ??= code;
                    if (currency != code) continue;
                    try { total += cost; } catch (OverflowException) { throw new InvalidDataException(); }
                }
            }
            if (currency is not null) windows.Add(new("spend", "Last 30 days", Unit: currency, DisplayValue: $"{total:N2} {currency}"));
        }
        else
        {
            var lot = Get(payloads.GetValueOrDefault("packs"), "currentLot"); var usageRoot = payloads.GetValueOrDefault("usage");
            var usage = Get(usageRoot, "usage"); if (usage.ValueKind != JsonValueKind.Object) usage = usageRoot;
            double? total, used, left;
            if (Text(lot, "status")?.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) == true && Numeric(lot, "totalToken") is > 0)
            { total = Numeric(lot, "totalToken"); used = Numeric(lot, "consumedToken") ?? 0; left = total - used; }
            else { total = Numeric(usage, "totalToken"); used = Numeric(usage, "usedToken"); left = Numeric(usage, "availableToken"); used ??= total - left; left ??= total - used; }
            if (total is > 0 && used is >= 0) windows.Add(new("tokens", "Token quota", used / total * 100, Unit: "tokens", DisplayValue: $"{Math.Max(0, left ?? 0):N0} / {total:N0} tokens remaining"));
            var fuel = payloads.GetValueOrDefault("fuel"); var packs = Get(fuel, "list");
            if (Numeric(fuel, "totalQuota") is > 0 and var fuelTotal && packs.ValueKind == JsonValueKind.Array)
            {
                var balances = packs.EnumerateArray().Select(x => Numeric(x, "availableToken")).Where(x => x.HasValue).ToArray();
                var fuelLeft = balances.Length > 0 ? balances.Sum(x => x!.Value) : fuelTotal;
                var expires = packs.EnumerateArray().Select(x => EpochDate(x, "expireTime")).Where(x => x.HasValue).Min();
                windows.Add(new("fuel", "Fuel packs", Math.Max(0, fuelTotal - fuelLeft) / fuelTotal * 100, expires, Unit: "tokens", DisplayValue: $"{fuelLeft:N0} / {fuelTotal:N0} tokens remaining"));
            }
        }
        return Metered(id, windows);
    }
}
