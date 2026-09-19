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
    private static readonly HashSet<string> BrowserIds = new(StringComparer.Ordinal) { "mimo", "abacus", "stepfun", "sakana", "longcat", "mistral", "notion", "augment", "alibabatokenplan", "qwencloud" };
    public static string CredentialLabel(string id) => id switch
    {
        "mimo" => "Cookie header (api-platform_serviceToken and userId)",
        "alibabatokenplan" or "qwencloud" or "augment" or "abacus" or "sakana" or "longcat" or "mistral" => "Cookie header from the signed-in provider page",
        "stepfun" => "Oasis-Token (or Cookie header containing Oasis-Token)",
        "kimi" => "Kimi Code API key",
        "kiro" => "Kiro access token (normally detected from CLI)",
        "vertexai" => "OAuth access token (normally detected from gcloud ADC)",
        "gemini-cli" => "OAuth access token (normally detected from Gemini CLI sign-in)",
        "alibaba" => "Alibaba Coding Plan API key",
        "notion" => "Notion web cookie header (token_v2), or token_v2 value",
        "zed" => "Zed access token",
        "zoommate" => "Bearer token, or Cookie: followed by the ZoomMate cookie header",
        "groq" => "Enterprise API key with Prometheus metrics access",
        _ => "Provider key or access token"
    };
    private static string NormalizeBrowserCredential(string id, string credential)
    {
        if (credential.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) credential = credential[7..].Trim();
        if (id == "notion" && credential.Length >= 2 && (credential[0] == (char)39 && credential[^1] == (char)39 || credential[0] == (char)34 && credential[^1] == (char)34)) credential = credential[1..^1].Trim();
        var pairs = credential.Split(';').Select(x => x.Trim().Split('=', 2)).Where(x => x.Length == 2 && x[1].Length > 0).ToArray();
        if (id == "notion")
        {
            if (pairs.Length == 0) return "token_v2=" + credential;
            if (!pairs.Any(x => x[0] == "token_v2")) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        }
        if (id == "stepfun") return pairs.LastOrDefault(x => x[0] == "Oasis-Token")?[1] ?? credential;
        if (id != "mimo") return credential;
        var allowed = new[] { "api-platform_serviceToken", "userId", "api-platform_ph", "api-platform_slh" };
        if (!pairs.Any(x => x[0] == allowed[0]) || !pairs.Any(x => x[0] == allowed[1])) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        return string.Join("; ", pairs.Where(x => allowed.Contains(x[0], StringComparer.Ordinal)).DistinctBy(x => x[0]).Select(x => x[0] + "=" + x[1]));
    }
    private static string StepFunWebId(string token)
    {
        foreach (var half in token.Split("...", StringSplitOptions.None).Reverse())
        {
            var parts = half.Split('.'); if (parts.Length < 2 || parts[1].Length > 32768) continue;
            try
            {
                var encoded = parts[1].Replace('-', '+').Replace('_', '/');
                using var json = JsonDocument.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
                if (Text(json.RootElement, "device_id") is { Length: > 0 and <= 256 } id && id.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_')) return id;
            }
            catch (Exception error) when (error is FormatException or JsonException) { }
        }
        return "c8a1002d2c457e758785a9979832217c7c0b884c";
    }
    private static string MiMoBase(Func<string, string?> setting) => ManagementBase(setting("MIMO_API_URL") is { Length: > 0 } configured ? configured : "https://platform.xiaomimimo.com/api/v1");
    private static DateTimeOffset? ApiTime(JsonElement root, string key)
    {
        var number = Numeric(root, key);
        if (number is > 0 and <= 253402300799) return DateTimeOffset.FromUnixTimeSeconds((long)number.Value);
        return Date(Get(root, key));
    }
    private static ProviderReading ParseBrowser(string id, IReadOnlyDictionary<string, JsonElement> payloads)
    {
        var root = payloads.GetValueOrDefault("main"); var windows = new List<LimitWindow>(); string? plan = null;
        void Amount(string key, string name, double? amount, string currency)
        {
            if (amount.HasValue && double.IsFinite(amount.Value)) windows.Add(new(key, name, Unit: currency, DisplayValue: $"{amount:N2} {currency}"));
        }
        if (id == "mimo")
        {
            if (Numeric(root, "code") is 401 or 403) return new(id, ReadingState.NeedsAuth, []);
            if (Numeric(root, "code") != 0) throw new InvalidDataException();
            var data = Get(root, "data"); var currency = Text(data, "currency");
            if (string.IsNullOrWhiteSpace(currency) || currency.Length > 16 || Numeric(data, "balance") is null) throw new InvalidDataException();
            Amount("balance", "Available balance", Numeric(data, "balance"), currency);
            Amount("cash", "Cash balance", Numeric(data, "cashBalance"), currency);
            Amount("gift", "Gift balance", Numeric(data, "giftBalance"), currency);
            var detailRoot = payloads.GetValueOrDefault("detail");
            var detail = Numeric(detailRoot, "code") == 0 ? Get(detailRoot, "data") : default;
            plan = Text(detail, "planCode");
            DateTimeOffset? reset = null;
            if (DateTimeOffset.TryParseExact(Text(detail, "currentPeriodEnd"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) reset = parsed;
            var usageRoot = payloads.GetValueOrDefault("usage"); var items = Get(Get(Get(usageRoot, "data"), "monthUsage"), "items");
            if (Numeric(usageRoot, "code") == 0 && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    if (Numeric(item, "limit") is > 0 and var limit && Numeric(item, "used") is >= 0 and var used)
                        windows.Insert(0, new("credits." + windows.Count, Text(item, "name") ?? "Monthly credits", Numeric(item, "percent") is >= 0 and <= 1 and var fraction ? fraction * 100 : used / limit * 100, reset,
                            Unit: "credits", DisplayValue: $"{used:N0} / {limit:N0} credits"));
        }
        else if (id == "abacus")
        {
            if (Get(root, "success").ValueKind != JsonValueKind.True)
            {
                var error = Text(root, "error") ?? "";
                if (Pattern(error, @"expired|session|login|authenticate|unauthorized|forbidden").Success) return new(id, ReadingState.NeedsAuth, []);
                throw new InvalidDataException();
            }
            var result = Get(root, "result"); var total = Numeric(result, "totalComputePoints"); var left = Numeric(result, "computePointsLeft");
            if (total is not >= 0 || left is not >= 0) throw new InvalidDataException();
            var billingRoot = payloads.GetValueOrDefault("billing");
            var billing = Get(billingRoot, "success").ValueKind == JsonValueKind.True ? Get(billingRoot, "result") : default;
            plan = Text(billing, "currentTier");
            windows.Add(new("credits", "Compute credits", total > 0 ? Math.Max(0, total.Value - left.Value) / total * 100 : null,
                Date(Get(billing, "nextBillingDate")), Unit: "credits", DisplayValue: $"{left:N0} / {total:N0} credits remaining"));
        }
        else if (id == "stepfun")
        {
            if (Numeric(root, "status") != 1) throw new InvalidDataException();
            var fiveReset = ApiTime(root, "five_hour_usage_reset_time"); var weeklyReset = ApiTime(root, "weekly_usage_reset_time");
            var credit = Get(root, "plan_credit_rate_limit"); var buckets = Get(credit, "credit_buckets");
            var hasCredit = Numeric(credit, "subscription_credit_left_rate").HasValue || Numeric(credit, "topup_credit_left_rate").HasValue || buckets.ValueKind == JsonValueKind.Array && buckets.GetArrayLength() > 0;
            var creditPlan = !fiveReset.HasValue && !weeklyReset.HasValue && (hasCredit || Numeric(root, "plan_family") == 2);
            if (creditPlan)
            {
                double? left = null;
                if (buckets.ValueKind == JsonValueKind.Array && buckets.GetArrayLength() > 0)
                {
                    var balances = buckets.EnumerateArray().Select(x => (Total: Numeric(x, "credit_total"), Left: Numeric(x, "credit_residual"))).ToArray();
                    if (balances.All(x => x.Total is > 0 && x.Left is >= 0 && x.Left <= x.Total))
                        left = balances.Sum(x => x.Left!.Value) / balances.Sum(x => x.Total!.Value);
                }
                left ??= Numeric(credit, "subscription_credit_left_rate") ?? Numeric(credit, "topup_credit_left_rate");
                if (left is >= 0 and <= 1) windows.Add(new("credits", "Monthly credits", (1 - left) * 100, ApiTime(credit, "subscription_credit_reset_time")));
            }
            else
            {
                foreach (var (key, name, reset, minutes) in new[] { ("five_hour_usage_left_rate", "5h limit", fiveReset, 300), ("weekly_usage_left_rate", "Weekly limit", weeklyReset, 10080) })
                    if (reset.HasValue && Numeric(root, key) is >= 0 and <= 1 and var left) windows.Add(new(key, name, (1 - left) * 100, reset, minutes));
            }
            var status = payloads.GetValueOrDefault("status");
            if (Numeric(status, "status") == 1) plan = Text(Get(status, "subscription"), "name");
        }
        else if (id == "sakana")
        {
            var html = Text(root, "html") ?? ""; if (html.Length > 2 * 1024 * 1024) throw new InvalidDataException();
            foreach (var (label, key, minutes) in new[] { ("5-hour", "fivehour", 300), ("Weekly", "weekly", 10080) })
            {
                var heading = Pattern(html, @"<p[^>]*>\s*" + label + @"\s*</p>"); if (!heading.Success) continue;
                var tail = html[(heading.Index + heading.Length)..];
                var boundary = Pattern(tail, """<p[^>]*>\s*(?:5-hour|Weekly)\s*</p>|<div[^>]*data-slot=(?:"card"|'card'|"card-title"|'card-title')[^>]*>""");
                if (boundary.Success) tail = tail[..boundary.Index];
                var percent = Pattern(tail, @"<p[^>]*>\s*([0-9]+(?:\.[0-9]+)?)%\s+used\s*</p>");
                if (!percent.Success || TextNumber(percent.Groups[1].Value) is not >= 0 or > 100) continue;
                var resetText = Pattern(tail, @"<p[^>]*>\s*Resets on ([^<]+?)\s*</p>");
                DateTimeOffset? reset = null;
                if (resetText.Success && DateTimeOffset.TryParseExact(WebUtility.HtmlDecode(resetText.Groups[1].Value), "MMMM d, yyyy 'at' h:mm tt",
                    CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AssumeUniversal, out var parsed)) reset = parsed;
                windows.Add(new(key, label + " limit", TextNumber(percent.Groups[1].Value), reset, minutes));
            }
            var title = Pattern(html, """<div[^>]*data-slot="card-title"[^>]*>[\s\S]*?<span>\s*([^<]+?)\s*</span>""");
            if (title.Success) plan = WebUtility.HtmlDecode(title.Groups[1].Value);
            var payg = Text(payloads.GetValueOrDefault("payg"), "html") ?? "";
            var balance = Pattern(payg, """<h2[^>]*>\s*Credit balance\s*</h2>[\s\S]{0,900}?<p[^>]*tabular-nums[^"]*"[^>]*>\$?([0-9][0-9,]*(?:\.[0-9]+)?)</p>""");
            if (balance.Success) Amount("balance", "Pay as you go balance", TextNumber(balance.Groups[1].Value), "USD");
        }
        return Metered(id, windows, plan);
    }
}
