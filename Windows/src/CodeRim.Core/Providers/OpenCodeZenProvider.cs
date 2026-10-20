using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private const string ZenWorkspaces = "def39973159c7f0483d8793a822b8dbb10d067e12c65455fcb4608459ba0234f";
    private const string ZenSubscription = "7abeebee372f304e050aaaf92be863f4a86490e382f8c79db68fd94040d691b4";
    private const string ZenBilling = "c83b78a614689c38ebee981f9b39a8b377716db85c1fd7dbab604adc02d3313d";
    private static readonly string[] ZenPercent = ["usagePercent", "usedPercent", "percentUsed", "percent", "usage_percent", "used_percent", "utilization", "utilizationPercent", "utilization_percent", "usage"];
    private static readonly string[] ZenRelativeReset = ["resetInSec", "resetInSeconds", "resetSeconds", "reset_sec", "reset_in_sec", "resetsInSec", "resetsInSeconds", "resetIn", "resetSec"];
    private static readonly string[] ZenAbsoluteReset = ["resetAt", "resetsAt", "reset_at", "resets_at", "nextReset", "next_reset", "renewAt", "renew_at"];
    private static readonly string[] ZenUsedKeys = ["used", "usage", "consumed", "count", "usedTokens"];
    private static readonly string[] ZenLimitKeys = ["limit", "total", "quota", "max", "cap", "tokenLimit"];
    private static Match ZenMatch(string text, string expression) => Regex.Match(text, expression, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static string ZenField(string field) => @"(?<![\w$])(?:""" + Regex.Escape(field) + @"""|" + Regex.Escape(field) + @")\s*:\s*(?:\$R\[\d+\]\s*=\s*)?";
    private static bool ZenSignedOut(string text) => text.Contains("login", StringComparison.OrdinalIgnoreCase)
        || text.Contains("sign in", StringComparison.OrdinalIgnoreCase) || text.Contains("auth/authorize", StringComparison.OrdinalIgnoreCase)
        || text.Contains("not associated with an account", StringComparison.OrdinalIgnoreCase) || text.Contains("actor of type \"public\"", StringComparison.OrdinalIgnoreCase);
    private static string ZenText(JsonElement response)
    {
        var text = Text(response, "text") ?? ""; if (text.Length > 2 * 1024 * 1024) throw new InvalidDataException();
        if (ZenSignedOut(text)) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        return text;
    }
    private static string? ZenWorkspace(string? value, bool response = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > (response ? 2 * 1024 * 1024 : 2048)) return null;
        var match = ZenMatch(value, @"(?<![A-Za-z0-9_])wrk_[A-Za-z0-9]+(?![A-Za-z0-9_])"); return match.Success ? match.Value : null;
    }
    private static IEnumerable<JsonElement> ZenObjects(JsonElement root, int depth = 0)
    {
        if (depth > 16) yield break;
        if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
            foreach (var child in root.EnumerateObject()) foreach (var item in ZenObjects(child.Value, depth + 1)) yield return item;
        }
        else if (root.ValueKind == JsonValueKind.Array) foreach (var child in root.EnumerateArray()) foreach (var item in ZenObjects(child, depth + 1)) yield return item;
    }
    private static ProviderReading? ZenSubscriptionReading(string text)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            using var json = JsonDocument.Parse(text);
            var candidates = ZenCandidates(json.RootElement, now).ToArray();
            var rolling = candidates.Where(x => ZenMatch(x.Path, @"rolling|hour|5h|primary").Success).ToArray();
            if (rolling.Length == 0) rolling = candidates;
            if (rolling.Length > 0)
            {
                var first = rolling.OrderBy(x => x.Window.ResetsAt ?? now).ThenByDescending(x => x.Window.UsedPercent).First();
                var remaining = candidates.Where(x => x.Path != first.Path).ToArray();
                var weekly = remaining.Where(x => ZenMatch(x.Path, @"weekly|week|secondary").Success).ToArray();
                if (weekly.Length == 0) weekly = remaining;
                if (weekly.Length > 0)
                {
                    var second = weekly.OrderByDescending(x => x.Window.ResetsAt ?? now).ThenByDescending(x => x.Window.UsedPercent).First();
                    return new("opencode-zen", ReadingState.Ready,
                        [first.Window with { Id = "rolling", Name = "5h limit", DurationMinutes = 300 },
                         second.Window with { Id = "weekly", Name = "Weekly limit", DurationMinutes = 10080 }], now, Plan: "Subscription");
                }
            }
        }
        catch (JsonException) { }
        var windows = new List<LimitWindow>();
        foreach (var (key, label, minutes) in new[] { ("rollingUsage", "5h limit", 300), ("weeklyUsage", "Weekly limit", 10080) })
        {
            var chunk = ZenMatch(text, ZenField(key) + @"\{([^}]{0,16384})\}");
            if (!chunk.Success) return null;
            var percent = ZenNumber(chunk.Groups[1].Value, "usagePercent"); var seconds = ZenNumber(chunk.Groups[1].Value, "resetInSec");
            if (percent is not >= 0 or > 100 || seconds is not >= 0 or > 31622400) return null;
            windows.Add(new(key, label, percent, now.AddSeconds(seconds.Value), minutes));
        }
        return new("opencode-zen", ReadingState.Ready, windows, now, Plan: "Subscription");
    }
    private static DateTimeOffset? ZenDate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
            return Date(JsonSerializer.SerializeToElement(number));
        return Date(value);
    }
    private static IEnumerable<(string Path, LimitWindow Window)> ZenCandidates(JsonElement root, DateTimeOffset now, string path = "", DateTimeOffset? inherited = null, int depth = 0)
    {
        if (depth > 16) yield break;
        if (root.ValueKind == JsonValueKind.Array)
        {
            var index = 0; foreach (var child in root.EnumerateArray())
                foreach (var item in ZenCandidates(child, now, path + "/" + index++, inherited, depth + 1)) yield return item;
        }
        if (root.ValueKind != JsonValueKind.Object) yield break;
        var renews = ZenDate(Get(root, "renewAt")) ?? ZenDate(Get(root, "renew_at")) ?? inherited;
        var percent = ZenPercent.Select(x => Numeric(root, x)).FirstOrDefault(x => x.HasValue);
        if (percent is >= 0 and <= 1) percent *= 100;
        if (percent is null)
        {
            var used = ZenUsedKeys.Select(x => Numeric(root, x)).FirstOrDefault(x => x.HasValue);
            var limit = ZenLimitKeys.Select(x => Numeric(root, x)).FirstOrDefault(x => x.HasValue);
            if (used is >= 0 && limit is > 0) percent = used.Value / limit.Value * 100;
        }
        if (percent.HasValue && double.IsFinite(percent.Value))
        {
            var seconds = ZenRelativeReset.Select(x => Numeric(root, x)).FirstOrDefault(x => x.HasValue);
            DateTimeOffset? reset = seconds is >= 0 and <= 31622400 ? now.AddSeconds(seconds.Value) : null;
            reset ??= ZenAbsoluteReset.Select(x => ZenDate(Get(root, x))).FirstOrDefault(x => x.HasValue) ?? renews;
            yield return (path.ToLowerInvariant(), new("", "", Math.Clamp(percent.Value, 0, 100), reset));
        }
        foreach (var child in root.EnumerateObject())
            foreach (var item in ZenCandidates(child.Value, now, path + "/" + child.Name, renews, depth + 1)) yield return item;
    }
    private static double? ZenNumber(string text, string field)
    {
        var match = ZenMatch(text, ZenField(field) + @"(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)");
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
    }
    private static ProviderReading ZenBillingReading(string text)
    {
        double? usage = null, limit = null, balance = null; var hasSubscription = false; var customerFound = false;
        try
        {
            using var json = JsonDocument.Parse(text);
            foreach (var customer in ZenObjects(json.RootElement))
            {
                if (Text(customer, "customerID") is not { Length: > 0 }) continue;
                customerFound = true; usage = Numeric(customer, "monthlyUsage"); limit = Numeric(customer, "monthlyLimit"); balance = Numeric(customer, "balance");
                hasSubscription = Get(customer, "subscription").ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined; break;
            }
        }
        catch (JsonException)
        {
            customerFound = ZenMatch(text, ZenField("customerID") + @"""[^""]+""").Success;
            if (customerFound)
            {
                usage = ZenNumber(text, "monthlyUsage"); limit = ZenNumber(text, "monthlyLimit"); balance = ZenNumber(text, "balance");
                var subscription = ZenMatch(text, ZenField("subscription") + @"([^,}]+)");
                hasSubscription = subscription.Success && subscription.Groups[1].Value.Trim() != "null";
            }
        }
        if (!customerFound || usage is null || hasSubscription) throw new InvalidDataException("No OpenCode Zen billing allowance was returned.");
        var used = usage.Value / 100_000_000; var now = DateTimeOffset.UtcNow;
        var windows = new List<LimitWindow> { new("monthly-spend", "Monthly spending", limit is > 0 ? Math.Clamp(used / limit.Value * 100, 0, 100) : null,
            Unit: "USD", DisplayValue: limit is > 0 ? $"{used:N2} / {limit:N2} USD" : $"{used:N2} USD") };
        if (balance.HasValue) windows.Add(new("balance", "Available balance", Unit: "USD", DisplayValue: $"{balance.Value / 100_000_000:N2} USD"));
        return new("opencode-zen", ReadingState.Ready, windows, now, Plan: "Pay as you go");
    }
    private static async Task<ProviderReading> FetchOpenCodeZen(Func<string, string?> setting,
        Func<string, IReadOnlyDictionary<string, string>, string?, Task<JsonElement>> get)
    {
        var configured = setting("OPENCODE_WORKSPACE_ID") ?? setting("CODEXBAR_OPENCODE_WORKSPACE_ID") ?? setting("OPENCODE_ZEN_WORKSPACE_ID");
        var workspace = ZenWorkspace(configured);
        if (configured is not null && workspace is null) throw new InvalidDataException("Enter an OpenCode workspace ID or workspace URL.");
        async Task<string> Read(string server, bool post = false)
        {
            var args = workspace is null ? "[]" : JsonSerializer.Serialize(new[] { workspace });
            var url = "https://opencode.ai/_server" + (post ? "" : "?id=" + server + (workspace is null ? "" : "&args=" + Uri.EscapeDataString(args)));
            var headers = new Dictionary<string, string> { ["X-Server-Id"] = server, ["X-Server-Instance"] = "server-fn:" + Guid.NewGuid(),
                ["Origin"] = "https://opencode.ai", ["Referer"] = workspace is null ? "https://opencode.ai" : "https://opencode.ai/workspace/" + workspace + "/billing" };
            return ZenText(await get(url, headers, post ? args : null).ConfigureAwait(false));
        }
        if (workspace is null)
        {
            workspace = ZenWorkspace(await Read(ZenWorkspaces).ConfigureAwait(false), response: true);
            workspace ??= ZenWorkspace(await Read(ZenWorkspaces, true).ConfigureAwait(false), response: true);
            if (workspace is null) throw new InvalidDataException("No OpenCode workspace was returned.");
        }
        try
        {
            var subscription = await Read(ZenSubscription).ConfigureAwait(false);
            if (ZenSubscriptionReading(subscription) is { } reading) return reading;
            var explicitNull = string.Equals(subscription.Trim().TrimEnd(';'), "null", StringComparison.OrdinalIgnoreCase)
                || ZenMatch(subscription, @"\]\s*=\s*\[\s*\]\s*,\s*null\s*\)\s*;?\s*$").Success || ZenMatch(subscription, @"^\s*\$R\[\d+\]\s*=\s*null\s*;?\s*$").Success;
            if (!explicitNull && ZenSubscriptionReading(await Read(ZenSubscription, true).ConfigureAwait(false)) is { } retried) return retried;
        }
        catch (ProviderRequestException error) when (error.Status is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden and not HttpStatusCode.TooManyRequests) { }
        return ZenBillingReading(await Read(ZenBilling).ConfigureAwait(false));
    }
}
