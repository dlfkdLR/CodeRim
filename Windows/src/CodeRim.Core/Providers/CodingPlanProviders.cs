using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

public sealed partial class NativeProviders
{
    private static string KimiEndpoint(Func<string, string?> setting)
    {
        var baseUrl = ManagementBase(setting("KIMI_CODE_BASE_URL") is { Length: > 0 } configured ? configured : "https://api.kimi.com");
        return baseUrl.EndsWith("/coding/v1", StringComparison.Ordinal) ? baseUrl + "/usages"
            : baseUrl.EndsWith("/coding", StringComparison.Ordinal) ? baseUrl + "/v1/usages" : baseUrl + "/coding/v1/usages";
    }
    private static ProviderReading ParseKimi(JsonElement root)
    {
        var windows = new List<LimitWindow>();
        foreach (var (key, name, minutes) in new[] { ("limit_5h", "5h limit", 300), ("limit_7d", "Weekly limit", 10080), ("limit_month_total", "Monthly limit", 0) })
        {
            var pool = Get(Get(root, "usages"), key);
            if (Number(pool, "used_ratio") is >= 0 and var ratio)
                windows.Add(new(key, name, Math.Min(1, ratio) * 100, Date(Get(pool, "reset_time")), minutes));
        }
        void Legacy(string key, string name, JsonElement detail, int minutes)
        {
            if (windows.Any(x => x.Id == key) || Numeric(detail, "limit") is not > 0) return;
            var limit = Numeric(detail, "limit")!.Value;
            var used = Numeric(detail, "used");
            if (used is null && Numeric(detail, "remaining") is >= 0 and var left && left <= limit) used = limit - left;
            if (used is not >= 0) return;
            var reset = Date(Get(detail, "resetTime")) ?? Date(Get(detail, "resetAt")) ?? Date(Get(detail, "reset_time")) ?? Date(Get(detail, "reset_at"));
            windows.Add(new(key, name, Math.Clamp(used.Value / limit * 100, 0, 100), reset, minutes));
        }
        Legacy("limit_7d", "Weekly limit", Get(root, "usage"), 10080);
        var limits = Get(root, "limits");
        if (limits.ValueKind == JsonValueKind.Array)
            foreach (var item in limits.EnumerateArray())
            {
                var window = Get(item, "window");
                var multiplier = Text(window, "timeUnit") switch { "TIME_UNIT_MINUTE" => 1, "TIME_UNIT_HOUR" => 60, "TIME_UNIT_DAY" => 1440, _ => 0 };
                var duration = Numeric(window, "duration") * multiplier;
                if (duration is not > 0 or > int.MaxValue) continue;
                var minutes = (int)duration.Value;
                Legacy(minutes == 300 ? "limit_5h" : minutes == 10080 ? "limit_7d" : "window." + minutes,
                    minutes == 300 ? "5h limit" : minutes == 10080 ? "Weekly limit" : minutes + " minute limit", Get(item, "detail"), minutes);
            }
        var plan = Text(Get(Get(root, "user"), "membership"), "level");
        if (plan == "LEVEL_UNSPECIFIED") plan = null;
        var version = Get(root, "version");
        if (version.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || Text(root, "version") == "GOODS_VERSION_V1")
            plan = plan switch { "LEVEL_FREE" => "Adagio", "LEVEL_TRIAL" => "Andante", "LEVEL_BASIC" => "Moderato", "LEVEL_INTERMEDIATE" => "Allegretto", "LEVEL_ADVANCED" => "Allegro", _ => plan };
        return Metered("kimi", windows.OrderBy(x => x.DurationMinutes == 300 ? 0 : x.DurationMinutes == 10080 ? 1 : 2).ToArray(), plan);
    }
    private static ProviderReading Metered(string id, IReadOnlyList<LimitWindow> windows, string? plan = null) =>
        new(id, windows.Count > 0 ? ReadingState.Ready : ReadingState.Unavailable, windows, DateTimeOffset.Now,
            windows.Count > 0 ? null : "No metered usage was returned for this account.", plan);

    private static Match Pattern(string text, string pattern) => Regex.Match(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static double? TextNumber(string value) => double.TryParse(value, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : null;
    private static ProviderReading ParseAmp(JsonElement root)
    {
        if (Text(Get(root, "error"), "code") == "auth-required") return new("amp", ReadingState.NeedsAuth, [], Message: "Sign in to Amp again.");
        if (Get(root, "ok").ValueKind != JsonValueKind.True || Text(Get(root, "result"), "displayText") is not { } raw) throw new InvalidDataException();
        if (raw.Length > 131072) throw new InvalidDataException();
        var text = Regex.Replace(raw, @"\x1B\[[0-?]*[ -/]*[@-~]", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)).Replace("**", "", StringComparison.Ordinal);
        const string amount = @"([0-9][0-9,]*(?:\.[0-9]+)?)";
        var windows = new List<LimitWindow>(); string? plan = null;
        var free = Pattern(text, @"^\s*Amp Free:\s*\$?" + amount + @"\s*/\s*\$?" + amount + @"\s+remaining");
        if (free.Success && TextNumber(free.Groups[1].Value) is { } remaining && TextNumber(free.Groups[2].Value) is > 0 and var limit)
            windows.Add(new("free", "Amp Free", Math.Clamp((limit - remaining) / limit * 100, 0, 100), Unit: "USD", DisplayValue: $"{remaining:N2} / {limit:N2} USD remaining"));
        else
        {
            free = Pattern(text, @"^\s*Amp Free:\s*" + amount + @"\s*%\s+remaining(?:\s+today)?");
            if (free.Success && TextNumber(free.Groups[1].Value) is { } left)
                windows.Add(new("free", "Amp Free", 100 - Math.Clamp(left, 0, 100), DurationMinutes: 1440));
        }
        var tier = Pattern(text, @"^\s*Amp\s+([^\r\n]+?)\s+Tier:\s*agent\s+usage\s+\$" + amount + @"\s+of\s+\$" + amount + @"\s+remaining\b([^\r\n]*?)resets\s+upon\s+renewal\s+in\s+([0-9][0-9,]*)\s+(days?|months?)\b");
        if (tier.Success && TextNumber(tier.Groups[2].Value) is { } agentLeft && TextNumber(tier.Groups[3].Value) is > 0 and var agentLimit)
        {
            plan = tier.Groups[1].Value.Trim();
            var period = Pattern(tier.Groups[4].Value, @"\bperiod\s+(\d{4}-\d{2}-\d{2})\s+to\s+(\d{4}-\d{2}-\d{2})\b");
            DateTimeOffset? reset = null; var minutes = 0;
            if (period.Success && DateTimeOffset.TryParseExact(period.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start)
                && DateTimeOffset.TryParseExact(period.Groups[2].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end)
                && end > start && (end - start).TotalMinutes <= int.MaxValue) { reset = end; minutes = (int)(end - start).TotalMinutes; }
            var renewal = $"renews in {tier.Groups[5].Value} {tier.Groups[6].Value}";
            windows.Insert(0, new("agent", "Agent usage", Math.Clamp((agentLimit - agentLeft) / agentLimit * 100, 0, 100), reset, minutes,
                Unit: "USD", DisplayValue: $"{agentLeft:N2} / {agentLimit:N2} USD remaining · {renewal}"));
            var orb = Pattern(tier.Groups[4].Value, @"\borb\s+usage\s+" + amount + @"h\s+of\s+" + amount + @"h\s+a1\.small\s+orb\s+hours\s+remaining\b");
            if (orb.Success && TextNumber(orb.Groups[1].Value) is { } orbLeft && TextNumber(orb.Groups[2].Value) is > 0 and var orbLimit)
                windows.Add(new("orb", "Orb hours", Math.Clamp((orbLimit - orbLeft) / orbLimit * 100, 0, 100), reset, minutes,
                    Unit: "hours", DisplayValue: $"{orbLeft:N2} / {orbLimit:N2} hours remaining"));
        }
        else
        {
            var subscription = Pattern(text, @"^\s*(?:Subscription\s+(.+?)|Amp\s+(.+?)\s+Subscription):\s*" + amount + @"\s*%\s+other\s+usage\s+and\s+" + amount + @"\s*%\s+orb\s+usage\s+remaining\s*-\s*resets\s+upon\s+renewal\s+in\s+([0-9][0-9,]*)\s+(days?|months?)");
            if (subscription.Success && TextNumber(subscription.Groups[3].Value) is { } other && TextNumber(subscription.Groups[4].Value) is { } orb)
            {
                plan = subscription.Groups[1].Success ? subscription.Groups[1].Value : subscription.Groups[2].Value;
                var renewal = $"Renews in {subscription.Groups[5].Value} {subscription.Groups[6].Value}";
                windows.Insert(0, new("agent", "Other usage", 100 - Math.Clamp(other, 0, 100), DisplayValue: renewal));
                windows.Add(new("orb", "Orb usage", 100 - Math.Clamp(orb, 0, 100), DisplayValue: renewal));
            }
        }
        var credits = Pattern(text, @"^\s*Individual credits:\s*\$?" + amount + @"\s+remaining");
        if (credits.Success && TextNumber(credits.Groups[1].Value) is { } credit)
            windows.Add(new("credits", "Individual credits", Unit: "USD", DisplayValue: $"{credit:N2} USD remaining"));
        foreach (Match workspace in Regex.Matches(text, @"^\s*Workspace\s+(.+?):\s*\$?" + amount + @"\s+remaining",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
            if (TextNumber(workspace.Groups[2].Value) is { } value)
                windows.Add(new("workspace." + windows.Count, workspace.Groups[1].Value, Unit: "USD", DisplayValue: $"{value:N2} USD remaining"));
        return Metered("amp", windows, plan);
    }
}
