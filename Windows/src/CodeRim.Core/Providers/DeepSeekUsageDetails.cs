using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

public static class DeepSeekUsageDetails
{
    public static bool Enabled(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    private static bool TokenCategory(string name) => name is "PROMPT_CACHE_HIT_TOKEN" or "PROMPT_CACHE_MISS_TOKEN" or "RESPONSE_TOKEN";
    private static InvalidDataException Invalid() => new("DeepSeek usage details have an unsupported shape.");
    private static JsonElement Object(JsonElement value)
    { if (value.ValueKind != JsonValueKind.Object) throw Invalid(); return value; }
    private static JsonElement.ArrayEnumerator Array(JsonElement value, bool optional = false)
    {
        if (optional && value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return default;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 4096) throw Invalid();
        return value.EnumerateArray();
    }
    private static string? Name(JsonElement value, bool optional = true)
    {
        if (optional && value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString()?.Trim() is not { Length: > 0 and <= 256 } name
            || name.Any(char.IsControl)) throw Invalid();
        return name;
    }
    private static readonly Regex Numeric = new(@"\A(?<sign>[+-]?)(?<whole>[0-9]+)(?:\.(?<fraction>[0-9]+))?(?:[eE](?<exponent>[+-]?[0-9]+))?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static (string Digits, int Power) Exact(string text, bool allowNegative = false)
    {
        var match = Numeric.Match(text.Trim());
        if (!match.Success) throw Invalid();
        var exponent = 0;
        if (match.Groups["exponent"].Success && (!int.TryParse(match.Groups["exponent"].Value, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out exponent) || exponent is < -1024 or > 1024)) throw Invalid();
        var fraction = match.Groups["fraction"].Value;
        var digits = (match.Groups["whole"].Value + fraction).TrimStart('0');
        if (digits.Length == 0) return ("0", 0);
        if (match.Groups["sign"].Value == "-" && !allowNegative) throw Invalid();
        var power = exponent - fraction.Length;
        var trimmed = digits.TrimEnd('0'); power += digits.Length - trimmed.Length;
        return ((match.Groups["sign"].Value == "-" ? "-" : "") + trimmed, power);
    }
    internal static decimal Number(JsonElement value, bool allowNegative = false)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
        if (text is not { Length: > 0 and <= 128 }) throw Invalid();
        var exact = Exact(text, allowNegative);
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
            || !allowNegative && amount < 0 || exact != Exact(amount.ToString("G29", CultureInfo.InvariantCulture), allowNegative)) throw Invalid();
        return amount;
    }
    private static long Count(JsonElement value)
    {
        var number = Number(value);
        if (number > long.MaxValue || decimal.Truncate(number) != number) throw Invalid();
        return (long)number;
    }
    private static void Validate(JsonElement value)
    {
        var nodes = 0;
        void Visit(JsonElement item, int depth)
        {
            if (++nodes > 32768 || depth > 32) throw Invalid();
            if (item.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in item.EnumerateObject()) { if (!seen.Add(pair.Name)) throw Invalid(); Visit(pair.Value, depth + 1); }
            }
            else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) Visit(child, depth + 1);
        }
        Visit(value, 0);
    }
    internal static void CheckAuthentication(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        static void Code(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var code) && code is 40002 or 40003)
                throw new UnauthorizedAccessException("DeepSeek session expired.");
        }
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == "code") Code(property.Value);
            else if (property.Name == "data" && property.Value.ValueKind == JsonValueKind.Object)
                foreach (var field in property.Value.EnumerateObject())
                    if (field.Name == "biz_code") Code(field.Value);
        }
    }
    private static JsonElement Payload(JsonElement root)
    {
        Validate(root); Object(root);
        CheckCode(Get(root, "code"));
        var data = Object(Get(root, "data")); CheckCode(Get(data, "biz_code"));
        return Get(data, "biz_data");
    }
    private static void CheckCode(JsonElement code)
    {
        if (code.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return;
        if (code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var value)) throw Invalid();
        if (value is 40002 or 40003) throw new UnauthorizedAccessException("DeepSeek session expired.");
        if (value != 0) throw Invalid();
    }
    public static (DateTimeOffset Start, DateTimeOffset End, DateTimeOffset Today) Window(DateTimeOffset now)
    {
        // One fixed current offset applies to the complete API range, including DST boundaries.
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        return (today.AddDays(-29), today.AddDays(1), today);
    }
    private sealed class Totals
    {
        internal long Tokens, Requests;
        internal decimal? Cost;
        internal void Add(long tokens, long requests) { Tokens = checked(Tokens + tokens); Requests = checked(Requests + requests); }
        internal void Spend(decimal cost) => Cost = checked((Cost ?? 0) + cost);
    }
    private static List<LimitWindow> Rows(Totals today, Totals total, string? currency, string period, int keys, Dictionary<string, long> models)
    {
        string Cost(decimal? value) => value is null ? "—" : (currency == "USD" ? "$" : currency == "CNY" ? "¥" : currency + " ")
            + value.Value.ToString("F4", CultureInfo.InvariantCulture);
        var rows = new List<LimitWindow> {
            new("deepseek-today", "Today", DisplayValue: Cost(today.Cost) + " · " + today.Tokens.ToString("N0", CultureInfo.CurrentCulture) + " tokens", Group: "Usage"),
            new("deepseek-period", period, DisplayValue: Cost(total.Cost) + " · " + total.Tokens.ToString("N0", CultureInfo.CurrentCulture) + " tokens", Group: "Usage"),
            new("deepseek-requests", "Requests", DisplayValue: total.Requests.ToString(CultureInfo.InvariantCulture), Group: "Usage")
        };
        if (keys > 0) rows.Add(new("deepseek-keys", "API keys", DisplayValue: keys.ToString(CultureInfo.InvariantCulture), Group: "Usage"));
        if (models.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).FirstOrDefault().Key is { } model)
            rows.Add(new("deepseek-model", "Top model", DisplayValue: model, Group: "Usage"));
        return rows;
    }
    private static string? Currency(JsonElement block)
    {
        var currency = Name(Get(block, "currency"));
        if (currency is not null && (currency.Length != 3 || !currency.All(char.IsAsciiLetter))) throw Invalid();
        return currency?.ToUpperInvariant();
    }
    private static string? Key(JsonElement series)
    {
        var value = Get(series, "api_key");
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind == JsonValueKind.String) return Name(value);
        Object(value); return Name(Get(value, "tracking_id")) ?? Name(Get(value, "name"));
    }
    public static IReadOnlyList<LimitWindow> ParseByKey(JsonElement amount, JsonElement cost, DateTimeOffset now)
    {
        CheckAuthentication(amount); CheckAuthentication(cost);
        var data = Object(Payload(amount)); var costs = Object(Payload(cost));
        var window = Window(now); var total = new Totals(); var today = new Totals();
        var models = new Dictionary<string, long>(StringComparer.Ordinal); var keys = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<(bool, string, string, long)>(); var anonymous = 0;
        foreach (var series in Array(Get(data, "series")))
        {
            Object(series); var model = Name(Get(series, "model")) ?? "unknown"; var key = Key(series);
            if (key is not null) keys.Add(key);
            var identity = key ?? (anonymous++).ToString(CultureInfo.InvariantCulture);
            foreach (var bucket in Array(Get(series, "buckets"), true))
            {
                Object(bucket); var timestamp = Count(Get(bucket, "time"));
                if (timestamp < window.Start.ToUnixTimeSeconds() || timestamp >= window.End.ToUnixTimeSeconds()) continue;
                if (!seen.Add((key is null, identity, model, timestamp))) throw Invalid();
                var usage = Object(Get(bucket, "usage")); long tokens = 0, requests = 0;
                var categories = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in usage.EnumerateObject())
                {
                    var category = pair.Name.ToUpperInvariant();
                    if (!categories.Add(category)) throw Invalid();
                    if (TokenCategory(category)) tokens = checked(tokens + Count(pair.Value));
                    else if (category == "REQUEST") requests = Count(pair.Value);
                }
                total.Add(tokens, requests); if (timestamp >= window.Today.ToUnixTimeSeconds()) today.Add(tokens, requests);
                models[model] = checked(models.GetValueOrDefault(model) + tokens);
            }
        }
        var blocks = Array(Get(costs, "data")).Select(block => {
            Object(block); var currency = Currency(block); decimal spend = 0;
            foreach (var series in Array(Get(block, "series"), true))
                foreach (var bucket in Array(Get(Object(series), "buckets"), true))
                {
                    var time = Count(Get(Object(bucket), "time"));
                    if (time >= window.Start.ToUnixTimeSeconds() && time < window.End.ToUnixTimeSeconds()) spend = checked(spend + Number(Get(bucket, "cost")));
                }
            return (Block: block, Currency: currency, Spend: spend);
        }).ToArray();
        if (blocks.Where(block => block.Currency is not null).Select(block => block.Currency).Distinct().Count()
            != blocks.Count(block => block.Currency is not null)) throw Invalid();
        var chosen = blocks.OrderByDescending(block => block.Currency == "USD" && block.Spend > 0)
            .ThenByDescending(block => block.Spend > 0).ThenByDescending(block => block.Currency == "USD").FirstOrDefault();
        var seenCost = new HashSet<(bool, string, string, long)>(); anonymous = 0;
        if (chosen.Block.ValueKind == JsonValueKind.Object)
            foreach (var series in Array(Get(chosen.Block, "series"), true))
            {
                Object(series); var key = Key(series); if (key is not null) keys.Add(key);
                var identity = key ?? (anonymous++).ToString(CultureInfo.InvariantCulture);
                var model = Name(Get(series, "model")) ?? "unknown";
                foreach (var bucket in Array(Get(series, "buckets"), true))
                {
                    var time = Count(Get(Object(bucket), "time"));
                    if (time < window.Start.ToUnixTimeSeconds() || time >= window.End.ToUnixTimeSeconds()) continue;
                    if (!seenCost.Add((key is null, identity, model, time))) throw Invalid();
                    var value = Number(Get(bucket, "cost"));
                    if (chosen.Currency is null) continue; // An unlabeled amount is not dollars or yuan.
                    total.Spend(value); if (time >= window.Today.ToUnixTimeSeconds()) today.Spend(value);
                }
            }
        return Rows(today, total, chosen.Currency, "Last 30 days", keys.Count, models);
    }
    public static IReadOnlyList<LimitWindow> ParseMonthly(JsonElement amount, JsonElement cost, DateTimeOffset now)
    {
        now = now.ToUniversalTime();
        CheckAuthentication(amount); CheckAuthentication(cost);
        var data = Object(Payload(amount)); var blocks = Array(Payload(cost)).ToArray();
        foreach (var block in blocks)
        {
            Object(block); _ = Currency(block);
            foreach (var value in Values(Get(block, "total"))) _ = Number(value.Amount);
            foreach (var day in Array(Get(block, "days")))
            {
                Object(day);
                if (!DateOnly.TryParseExact(Name(Get(day, "date"), false), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw Invalid();
                foreach (var value in Values(Get(day, "data"))) _ = Number(value.Amount);
            }
        }
        var selectedCost = blocks.FirstOrDefault(); var currency = selectedCost.ValueKind == JsonValueKind.Object ? Currency(selectedCost) : null;
        var total = new Totals(); var today = new Totals(); var models = new Dictionary<string, long>(StringComparer.Ordinal);
        static IEnumerable<(string Model, string Type, JsonElement Amount)> Values(JsonElement rows)
        {
            var seenModels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in Array(rows, true))
            {
                var model = Name(Get(Object(row), "model")) ?? "unknown";
                if (!seenModels.Add(model)) throw Invalid();
                var seenTypes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in Array(Get(row, "usage"), true))
                {
                    var type = Name(Get(Object(item), "type"), false)!.ToUpperInvariant();
                    if (!seenTypes.Add(type)) throw Invalid();
                    if (TokenCategory(type) || type == "REQUEST") yield return (model, type, Get(item, "amount"));
                }
            }
        }
        foreach (var row in Values(Get(data, "total")))
            if (TokenCategory(row.Type)) models[row.Model] = checked(models.GetValueOrDefault(row.Model) + Count(row.Amount));
        void Days(JsonElement days, bool money)
        {
            var seen = new HashSet<DateOnly>();
            foreach (var day in Array(days))
            {
                var dateText = Name(Get(Object(day), "date"), false);
                if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || !seen.Add(date)) throw Invalid();
                if (date.Year != now.Year || date.Month != now.Month || date > DateOnly.FromDateTime(now.Date)) continue;
                var current = date == DateOnly.FromDateTime(now.Date);
                foreach (var row in Values(Get(day, "data")))
                {
                    if (money)
                    {
                        if (!TokenCategory(row.Type)) continue;
                        var number = Number(row.Amount); if (currency is null) continue;
                        total.Spend(number); if (current) today.Spend(number);
                    }
                    else
                    {
                        var count = Count(row.Amount); var tokens = TokenCategory(row.Type) ? count : 0; var requests = row.Type == "REQUEST" ? count : 0;
                        total.Add(tokens, requests); if (current) today.Add(tokens, requests);
                    }
                }
            }
        }
        Days(Get(data, "days"), false);
        if (selectedCost.ValueKind == JsonValueKind.Object) Days(Get(selectedCost, "days"), true);
        return Rows(today, total, currency, "This month", 0, models);
    }
}
