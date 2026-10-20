using System.Globalization;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

public static class DeepSeekBalance
{
    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => names.Add(property.Name));
    }
    private static decimal Money(JsonElement value) => DeepSeekUsageDetails.Number(value, allowNegative: true);
    private static string Currency(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (text is not { Length: > 0 and <= 16 } || !text.All(char.IsAsciiLetter))
            throw new InvalidDataException("The provider balance has no valid currency.");
        return text.ToUpperInvariant();
    }
    private static int? Code(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var code))
            throw new InvalidDataException("The provider response has an invalid status.");
        return code;
    }
    public static ProviderReading Parse(JsonElement root, string source)
    {
        ProviderReading Error() => new("deepseek", ReadingState.Error, [], Message: "DeepSeek could not return a valid balance.");
        try
        {
            if (!Unique(root) || source is not "api" and not "web") return Error();
            var balances = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var paid = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var granted = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var unavailableForCalls = false;
            if (source == "web")
            {
                var data = Get(root, "data");
                // Authentication codes remain meaningful even when an error envelope's data is not a success object.
                var code = Code(Get(root, "code"));
                if (code is 40002 or 40003) return new("deepseek", ReadingState.NeedsAuth, []);
                if (code is not null and not 0 || !Unique(data)) return Error();
                var bizCode = Code(Get(data, "biz_code"));
                if (bizCode is 40002 or 40003) return new("deepseek", ReadingState.NeedsAuth, []);
                var summary = Get(data, "biz_data");
                if (bizCode is not null and not 0 || !Unique(summary)) return Error();
                foreach (var key in new[] { "normal_wallets", "bonus_wallets" })
                {
                    var wallets = Get(summary, key);
                    if (wallets.ValueKind != JsonValueKind.Array || wallets.GetArrayLength() > 1024) return Error();
                    foreach (var wallet in wallets.EnumerateArray())
                    {
                        if (!Unique(wallet)) return Error();
                        var currency = Currency(Get(wallet, "currency")); var amount = Money(Get(wallet, "balance"));
                        balances[currency] = checked(balances.GetValueOrDefault(currency) + amount);
                        var detail = key == "normal_wallets" ? paid : granted;
                        detail[currency] = checked(detail.GetValueOrDefault(currency) + amount);
                    }
                }
                foreach (var currency in balances.Keys) { paid.TryAdd(currency, 0); granted.TryAdd(currency, 0); }
            }
            else
            {
                var available = Get(root, "is_available");
                if (available.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Undefined)) return Error();
                unavailableForCalls = available.ValueKind == JsonValueKind.False;
                var rows = Get(root, "balance_infos");
                if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 1024) return Error();
                foreach (var row in rows.EnumerateArray())
                {
                    if (!Unique(row)) return Error();
                    var currency = Currency(Get(row, "currency"));
                    if (balances.ContainsKey(currency)) return Error();
                    var amount = Money(Get(row, "total_balance"));
                    if (row.TryGetProperty("granted_balance", out var bonus)) granted[currency] = Money(bonus);
                    if (row.TryGetProperty("topped_up_balance", out var topup)) paid[currency] = Money(topup);
                    balances.Add(currency, amount);
                }
            }
            if (balances.Count > 16) return Error();
            string Amount(decimal value, string currency) => value.ToString("N2", CultureInfo.CurrentCulture) + " " + currency;
            string Detail(string currency)
            {
                var values = new List<string>();
                if (paid.TryGetValue(currency, out var topup)) values.Add("Paid: " + Amount(topup, currency));
                if (granted.TryGetValue(currency, out var bonus)) values.Add("Granted: " + Amount(bonus, currency));
                return values.Count == 0 ? "" : " (" + string.Join(" / ", values) + ")";
            }
            var windows = balances.OrderByDescending(pair => pair.Value > 0 && pair.Key == "USD")
                .ThenByDescending(pair => pair.Value > 0).ThenByDescending(pair => pair.Key == "USD").ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new LimitWindow(pair.Key, unavailableForCalls ? "Total balance" : "Available balance", Unit: pair.Key,
                    DisplayValue: Amount(pair.Value, pair.Key) + Detail(pair.Key))).ToArray();
            return new("deepseek", ReadingState.Ready, windows, DateTimeOffset.Now,
                unavailableForCalls ? "Balance unavailable for API calls." : windows.Length == 0 ? "No balance wallets were returned for this account." : null);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { return Error(); }
    }
}
