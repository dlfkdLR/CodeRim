using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private const string PersonalApiPrefix = "zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/";
    private sealed record TokenPlanRegion(string Origin, string Gateway, string Region, string Site, string Product, bool Personal, string Dashboard, string Action);
    private static TokenPlanRegion TokenPlanConfig(string id, Func<string, string?> setting)
    {
        if (id == "qwencloud") return new("https://home.qwencloud.com", "https://cs-data.qwencloud.com", "ap-southeast-1", "QWENCLOUD",
            "sfm_tokenplansolo_public_intl", true, "https://home.qwencloud.com/billing/subscription/token-plan-individual", "IntlBroadScopeAspnGateway");
        var variant = (setting("ALIBABA_TOKEN_PLAN_REGION") ?? "intl").Trim().ToLowerInvariant();
        if (variant is not ("intl" or "cn" or "intl-personal" or "cn-personal")) throw new InvalidDataException("Select an Alibaba Token Plan region.");
        var cn = variant.StartsWith("cn", StringComparison.Ordinal); var personal = variant.EndsWith("-personal", StringComparison.Ordinal);
        var origin = cn ? "https://bailian.console.aliyun.com" : "https://modelstudio.console.alibabacloud.com";
        var region = cn ? "cn-beijing" : "ap-southeast-1";
        return new(origin, personal ? cn ? "https://bailian-cs.console.aliyun.com" : "https://bailian-singapore-cs.alibabacloud.com" : origin,
            region, cn ? "BAILIAN_ALIYUN" : "MODELSTUDIO_ALBABACLOUD",
            "sfm_tokenplan" + (personal ? "solo_public" : "teams_dp") + (cn ? "_cn" : "_intl"), personal,
            origin + "/" + region + "/?tab=plan#/efm/subscription/token-plan" + (personal ? "/personal" : ""),
            cn ? "BroadScopeAspnGateway" : "IntlBroadScopeAspnGateway");
    }
    private static string? TokenPlanCookie(string cookie, string name) => cookie.Split(';').Select(x => x.Trim().Split('=', 2)).FirstOrDefault(x => x.Length == 2 && x[0] == name)?[1];
    private static void ConfigureTokenPlan(HttpRequestMessage request, string id, string credential, Func<string, string?> setting, string? sec)
    {
        var config = TokenPlanConfig(id, setting); var cookie = NormalizeBrowserCredential(id, credential);
        request.Headers.Add("Origin", config.Origin); request.Headers.Referrer = new Uri(config.Dashboard); request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var csrf = TokenPlanCookie(cookie, "login_aliyunid_csrf") ?? TokenPlanCookie(cookie, "csrf");
        if (csrf is not null) { request.Headers.Add("x-xsrf-token", csrf); request.Headers.Add("x-csrf-token", csrf); }
        if (request.RequestUri!.AbsolutePath != "/data/api.json") return;
        request.Method = HttpMethod.Post; var fields = new Dictionary<string, string> { ["region"] = config.Region };
        if (config.Personal)
        {
            var api = request.RequestUri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]))["api"];
            var cornerstone = new Dictionary<string, object> { ["feTraceId"] = Guid.NewGuid().ToString(), ["feURL"] = config.Dashboard,
                ["protocol"] = "V2", ["console"] = "ONE_CONSOLE", ["productCode"] = "p_efm", ["domain"] = new Uri(config.Origin).Host,
                ["consoleSite"] = config.Site, ["userNickName"] = "", ["userPrincipalName"] = "", ["xsp_lang"] = "en-US" };
            if (id != "qwencloud") cornerstone["switchUserType"] = 3;
            if (TokenPlanCookie(cookie, "cna") is { } anonymous) cornerstone["X-Anonymous-Id"] = anonymous;
            var data = new Dictionary<string, object> { ["cornerstoneParam"] = cornerstone };
            if (api.EndsWith("/subscription", StringComparison.Ordinal)) data["commodityCode"] = config.Product;
            fields["product"] = "sfm_bailian"; fields["action"] = config.Action; fields["language"] = "en-US";
            fields["params"] = JsonSerializer.Serialize(new { Api = api, V = "1.0", Data = data });
        }
        else
        {
            fields["product"] = "BssOpenAPI-V3"; fields["action"] = "GetSubscriptionSummary";
            fields["params"] = JsonSerializer.Serialize(new { ProductCode = config.Product });
        }
        if (!string.IsNullOrWhiteSpace(sec)) fields["sec_token"] = sec;
        request.Content = new FormUrlEncodedContent(fields);
    }
    private static async Task<ProviderReading> FetchTokenPlan(string id, string credential, Func<string, string?> setting, Func<Uri, string?>? cookieForUri, Action<string> setSec, Func<string, Task<JsonElement>> get, CancellationToken token)
    {
        var config = TokenPlanConfig(id, setting);
        if (string.IsNullOrWhiteSpace(setting(id == "qwencloud" ? "QWEN_CLOUD_SEC_TOKEN" : "ALIBABA_TOKEN_PLAN_SEC_TOKEN")))
        {
            string? sec = null; var transient = false;
            try
            {
                var html = Text(await get(config.Dashboard).ConfigureAwait(false), "html") ?? "";
                var match = Regex.Match(html, """(?:secToken|sec_token|SEC_TOKEN|csrfToken)['"]?\s*[:=]\s*['"]([^'"]+)['"]""", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
                if (match.Success) sec = match.Groups[1].Value;
            }
            catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException) { token.ThrowIfCancellationRequested(); transient |= error is HttpRequestException or IOException or OperationCanceledException || error is ProviderRequestException http && (int)http.Status >= 500; }
            if (sec is null)
            {
                try { sec = ExpandedContexts(await get(config.Origin + "/tool/user/info.json").ConfigureAwait(false)).Select(x => Text(x, "secToken") ?? Text(x, "sec_token") ?? Text(x, "SEC_TOKEN") ?? Text(x, "csrfToken") ?? Text(x, "token")).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)); }
                catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException) { token.ThrowIfCancellationRequested(); transient |= error is HttpRequestException or IOException or OperationCanceledException || error is ProviderRequestException http && (int)http.Status >= 500; }
            }
            // The console uses a dashboard CSRF token in its paired gateway form.
            // Keep the Cookie headers scoped to each actual request; only this
            // named token can move between the selected region's fixed origins.
            string? ScopedSec(string url) => cookieForUri?.Invoke(new Uri(url)) is { } cookie
                ? TokenPlanCookie(NormalizeBrowserCredential(id, cookie), "sec_token") : null;
            sec ??= cookieForUri is null
                ? TokenPlanCookie(NormalizeBrowserCredential(id, credential), "sec_token")
                : ScopedSec(config.Dashboard) ?? ScopedSec(config.Gateway + "/data/api.json");
            if (sec is not null) setSec(sec);
            else if (id == "qwencloud") { if (transient) throw new IOException("The console is temporarily unavailable."); throw new ProviderRequestException(HttpStatusCode.Unauthorized); }
        }
        string Url(string kind) => config.Gateway + "/data/api.json?" + (config.Personal
            ? "action=" + config.Action + "&product=sfm_bailian&api=" + Uri.EscapeDataString(PersonalApiPrefix + kind) + "&_v=undefined"
            : "action=GetSubscriptionSummary&product=BssOpenAPI-V3&_tag=");
        var docs = new Dictionary<string, JsonElement>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            docs["main"] = await get(Url("usage")).ConfigureAwait(false); TokenPlanValidate(docs["main"]);
            if (!config.Personal || ExpandedContexts(docs["main"]).Any(x => Numeric(x, "per5HourPercentage").HasValue || Numeric(x, "per1WeekPercentage").HasValue) || attempt == 2) break;
            await Task.Delay(400, token).ConfigureAwait(false);
        }
        if (config.Personal)
            foreach (var kind in new[] { "subscription", "quota-config" })
            {
                try { var value = await get(Url(kind)).ConfigureAwait(false); TokenPlanValidate(value); docs[kind] = value; }
                catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException) { token.ThrowIfCancellationRequested(); }
            }
        return ParseTokenPlan(id, docs);
    }

    private static void TokenPlanNavigation(HttpRequestMessage request)
    {
        if (request.RequestUri!.AbsolutePath is "/data/api.json" or "/tool/user/info.json") return;
        request.Headers.Remove("X-Requested-With"); request.Headers.Accept.Clear();
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36");
        request.Headers.Add("Sec-Fetch-Site", "same-origin"); request.Headers.Add("Sec-Fetch-Mode", "navigate"); request.Headers.Add("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        request.Headers.Referrer = new Uri(request.RequestUri.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static readonly string[] PlanSuccessKeys = ["successResponse", "Success", "success"];
    private static void TokenPlanValidate(JsonElement root)
    {
        foreach (var frame in ExpandedContexts(root))
        {
            var code = Numeric(frame, "statusCode") ?? Numeric(frame, "status_code") ?? Numeric(frame, "code");
            var message = (Text(frame, "errorCode") ?? Text(frame, "Code") ?? Text(frame, "code") ?? "") + " " + (Text(frame, "errorMsg") ?? Text(frame, "Message") ?? Text(frame, "message") ?? Text(frame, "msg") ?? "");
            message = message.ToLowerInvariant();
            var failure = PlanSuccessKeys.Any(x => Get(frame, x).ValueKind == JsonValueKind.False || Text(frame, x) == "false");
            if (message.Contains("workspace.notauthoris", StringComparison.Ordinal) || message.Contains("workspace.notauthoriz", StringComparison.Ordinal)) throw new InvalidDataException("This workspace does not allow quota access.");
            if (code is 401 or 403 || message.Contains("login", StringComparison.Ordinal) || message.Contains("tokenerror", StringComparison.Ordinal) || message.Contains("unauthor", StringComparison.Ordinal)
                || message.Contains("notauthor", StringComparison.Ordinal) || message.Contains("forbidden", StringComparison.Ordinal) || message.Contains("request has expired", StringComparison.Ordinal))
                throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            if (failure || code.HasValue && code is not 0 and not 200) throw new InvalidDataException("The token plan request failed.");
        }
    }
    private static readonly string[] PlanNameKeys = ["specCode", "spec_code", "planName", "plan_name", "packageName", "commodityName", "SpecType", "ProductName", "productName"];
    private static readonly string[] PlanTotalKeys = ["totalQuota", "total_quota", "totalCredits", "totalCredit", "quota", "creditLimit", "creditsTotal", "monthlyTotalQuota", "amount", "totalValue", "TotalValue", "cycleTotalValue", "CycleTotalValue"];
    private static readonly string[] PlanUsedKeys = ["usedQuota", "used_quota", "usedCredits", "usedCredit", "consumedCredits", "usage", "used", "usedAmount", "consumeAmount", "usedValue", "UsedValue", "consumedValue", "ConsumedValue"];
    private static readonly string[] PlanRemainingKeys = ["remainingQuota", "remainQuota", "remainingCredits", "remainingCredit", "availableCredits", "balance", "remaining", "availableAmount", "remainAmount", "totalSurplusValue", "TotalSurplusValue", "surplusValue", "SurplusValue", "cycleSurplusValue", "CycleSurplusValue"];
    private static readonly string[] PlanResetKeys = ["nextRefreshTime", "resetTime", "periodEndTime", "billingCycleEnd", "billCycleEndTime", "expireTime", "expirationTime", "endTime", "validEndTime", "instanceEndTime", "EndTime", "cycleEndTime", "CycleEndTime", "nearestExpireDate", "NearestExpireDate"];
    private static ProviderReading ParseTokenPlan(string id, IReadOnlyDictionary<string, JsonElement> docs)
    {
        var main = docs.GetValueOrDefault("main"); TokenPlanValidate(main); var frames = ExpandedContexts(main).ToArray();
        var subscription = ExpandedContexts(docs.GetValueOrDefault("subscription")).ToArray();
        var plan = subscription.Select(x => FirstText(x, PlanNameKeys)).FirstOrDefault(x => x is not null)
            ?? frames.Select(x => FirstText(x, PlanNameKeys)).FirstOrDefault(x => x is not null);
        var window = frames.FirstOrDefault(x => Numeric(x, "per5HourPercentage").HasValue || Numeric(x, "per1WeekPercentage").HasValue);
        var windows = new List<LimitWindow>();
        if (window.ValueKind == JsonValueKind.Object)
        {
            var quota = plan is null ? default : ExpandedContexts(docs.GetValueOrDefault("quota-config")).Select(x => Get(x, plan.ToLowerInvariant())).FirstOrDefault(x => x.ValueKind == JsonValueKind.Object);
            foreach (var (key, label, minutes, prefix, maximum) in new[] { ("five-hour", "5-hour limit", 300, "per5Hour", Numeric(quota, "five_hour") ?? Numeric(quota, "fiveHour")), ("weekly", "Weekly limit", 10080, "per1Week", Numeric(quota, "weekly")) })
                if (Numeric(window, prefix + "Percentage") is >= 0 and var ratio)
                    windows.Add(new(key, label, Math.Clamp(ratio * 100, 0, 100), EpochDate(window, prefix + "ResetTime"), minutes,
                        DisplayValue: maximum is > 0 ? $"{Math.Clamp(ratio * 100, 0, 100):N0}% used · allowance {maximum:N0}" : null));
        }
        else
        {
            var summary = frames.FirstOrDefault(x => PlanTotalKeys.Any(k => Numeric(x, k).HasValue) || PlanUsedKeys.Any(k => Numeric(x, k).HasValue) || PlanRemainingKeys.Any(k => Numeric(x, k).HasValue));
            var total = PlanTotalKeys.Select(x => Numeric(summary, x)).FirstOrDefault(x => x.HasValue);
            var remaining = PlanRemainingKeys.Select(x => Numeric(summary, x)).FirstOrDefault(x => x.HasValue);
            var used = PlanUsedKeys.Select(x => Numeric(summary, x)).FirstOrDefault(x => x.HasValue) ?? (total.HasValue && remaining.HasValue ? Math.Max(0, total.Value - remaining.Value) : null);
            var reset = PlanResetKeys.Select(x => EpochDate(summary, x)).FirstOrDefault(x => x.HasValue)
                ?? frames.SelectMany(frame => PlanResetKeys.Select(x => EpochDate(frame, x))).FirstOrDefault(x => x.HasValue);
            if (total == 0 && frames.Any(x => Numeric(x, "TotalCount") == 0 || Numeric(x, "totalCount") == 0))
                return new(id, ReadingState.Ready, [], DateTimeOffset.UtcNow, "No active Token Plan subscription.", plan);
            if (used is >= 0 && total is > 0) windows.Add(new("quota", "Token Plan allowance", Math.Clamp(used.Value / total.Value * 100, 0, 100), reset, Unit: "credits", DisplayValue: $"{used:N2} / {total:N2} credits"));
        }
        return Metered(id, windows, plan);
    }
}
