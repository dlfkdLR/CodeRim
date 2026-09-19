using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

/// <summary>Read-only ports of the macOS native adapters and pinned CodexBar billing readers.</summary>
public sealed partial class NativeProviders : IDisposable
{
    public static IReadOnlySet<string> Supported { get; } = new HashSet<string>(StringComparer.Ordinal)
        { "cursor", "grok", "opencode", "commandcode", "ollama", "fireworks", "deepinfra", "codebuff", "neuralwatt", "llmproxy", "litellm", "zenmux", "warp", "wayfinder", "ibmbob", "kimi", "amp", "mimo", "abacus", "stepfun", "sakana" };
    private readonly HttpClient client;
    private readonly ConcurrentDictionary<string, DateTimeOffset> retryAfter = new(StringComparer.Ordinal);
    public NativeProviders(HttpMessageHandler? handler = null) => client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
    public void Dispose() => client.Dispose();
    public async Task<ProviderReading> FetchAsync(string id, string? credential, Func<string, string?> setting, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (!Supported.Contains(id)) return new(id, ReadingState.Unsupported, []);
        if (id != "wayfinder" && (string.IsNullOrWhiteSpace(credential) || credential.Any(char.IsControl))) return new(id, ReadingState.NeedsAuth, [], Message: "Connect this provider in Settings or sign in to its CLI.");
        if (retryAfter.TryGetValue(id, out var retry) && retry > DateTimeOffset.Now) return new(id, ReadingState.Unavailable, [], Message: "Provider rate limit reached. Waiting before retrying.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        async Task<JsonElement> GetJson(string url, IReadOnlyDictionary<string, string>? extraHeaders = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (id == "codebuff" && new Uri(url).AbsolutePath == "/api/v1/usage")
            {
                request.Method = HttpMethod.Post; request.Content = new StringContent("""{"fingerprintId":"codexbar-usage"}""", System.Text.Encoding.UTF8, "application/json");
            }
            if (id == "amp")
            {
                request.Method = HttpMethod.Post; request.Content = new StringContent("""{"method":"userDisplayBalanceInfo","params":{}}""", System.Text.Encoding.UTF8, "application/json");
            }
            if (id == "stepfun" || id == "abacus" && new Uri(url).AbsolutePath.EndsWith("_getBillingInfo", StringComparison.Ordinal))
            { request.Method = HttpMethod.Post; request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"); }
            if (id == "warp")
            {
                request.Method = HttpMethod.Post; request.Content = new StringContent(WarpBody(), System.Text.Encoding.UTF8, "application/json");
                request.Headers.Add("x-warp-client-id", "warp-app"); request.Headers.Add("x-warp-os-category", "Windows");
                request.Headers.Add("x-warp-os-name", "Windows"); request.Headers.Add("x-warp-os-version", Environment.OSVersion.Version.ToString());
            }
            request.Headers.UserAgent.ParseAdd(id == "commandcode" ? "command-code-desktop" : id == "warp" ? "Warp/1.0" : "CodeRim/2.1.5");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (BrowserIds.Contains(id))
            {
                var normalized = NormalizeBrowserCredential(id, credential!);
                if (id == "stepfun")
                {
                    var webid = StepFunWebId(normalized);
                    request.Headers.Add("oasis-appid", "10300"); request.Headers.Add("oasis-platform", "web"); request.Headers.Add("oasis-webid", webid);
                    normalized = "Oasis-Token=" + normalized + "; Oasis-Webid=" + webid;
                }
                request.Headers.TryAddWithoutValidation("Cookie", normalized);
                if (id == "mimo")
                {
                    request.Headers.Add("x-timeZone", "UTC+00:00"); request.Headers.Add("Origin", "https://platform.xiaomimimo.com");
                    request.Headers.Referrer = new Uri("https://platform.xiaomimimo.com/#/console/balance");
                }
            }
            else if (id == "cursor") request.Headers.TryAddWithoutValidation("Cookie", credential);
            else if (id != "wayfinder") request.Headers.Authorization = new AuthenticationHeaderValue(id == "ibmbob" ? BobAuthorization(credential!) : "Bearer", credential);
            if (extraHeaders is not null) foreach (var pair in extraHeaders) request.Headers.Add(pair.Key, pair.Value);
            if (id == "grok") request.Headers.Add("X-XAI-Token-Auth", "xai-grok-cli");
            if (id == "commandcode") request.Headers.Add("x-command-code-version", "desktop");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                retryAfter[id] = response.Headers.RetryAfter?.Date ?? DateTimeOffset.Now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            if (BrowserIds.Contains(id) && (int)response.StatusCode is >= 300 and < 400) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            if (!response.IsSuccessStatusCode) throw new ProviderRequestException(response.StatusCode);
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException();
            using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var output = new MemoryStream(); var buffer = new byte[16384]; int count;
            while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > 2 * 1024 * 1024) throw new InvalidDataException();
                output.Write(buffer, 0, count);
            }
            using var json = id == "sakana" ? JsonDocument.Parse(JsonSerializer.Serialize(new { html = System.Text.Encoding.UTF8.GetString(output.ToArray()) })) : JsonDocument.Parse(output.ToArray());
            return json.RootElement.Clone();
        }
        try
        {
            if (id == "ibmbob") return await FetchBob(GetJson).ConfigureAwait(false);
            if (ManagementIds.Contains(id)) return Parse(id, await ManagementPayloads(id, setting, url => GetJson(url)).ConfigureAwait(false));
            var endpoint = id switch
            {
                "cursor" => "https://cursor.com/api/usage-summary", "grok" => "https://cli-chat-proxy.grok.com/v1/billing?format=credits",
                "opencode" => "https://opencode.ai/zen/go/v1/usage", "ollama" => "https://ollama.com/api/usage",
                "deepinfra" => "https://api.deepinfra.com/payment/checklist?compute_owed=true",
                "codebuff" => "https://www.codebuff.com/api/v1/usage", "neuralwatt" => "https://api.neuralwatt.com/v1/quota",
                "commandcode" => "https://api.commandcode.ai/alpha/whoami",
                "mimo" => MiMoBase(setting) + "/balance", "abacus" => "https://apps.abacus.ai/api/_getOrganizationComputePoints",
                "stepfun" => "https://platform.stepfun.com/api/step.openapi.devcenter.Dashboard/QueryStepPlanRateLimit", "sakana" => "https://console.sakana.ai/billing",
                "kimi" => KimiEndpoint(setting), "amp" => "https://ampcode.com/api/internal?userDisplayBalanceInfo", _ => ""
            };
            if (id == "fireworks")
            {
                var slug = setting("FIREWORKS_ACCOUNT_SLUG");
                if (string.IsNullOrWhiteSpace(slug) || slug.Length > 128 || slug.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
                    return new(id, ReadingState.NeedsAuth, [], Message: "Set the Fireworks account slug in Settings.");
                var now = DateTimeOffset.UtcNow;
                endpoint = "https://api.fireworks.ai/v1/accounts/" + slug + "/billing/summary?startTime=" + Uri.EscapeDataString(now.AddDays(-30).ToString("O", CultureInfo.InvariantCulture)) + "&endTime=" + Uri.EscapeDataString(now.ToString("O", CultureInfo.InvariantCulture));
            }
            documents["main"] = await GetJson(endpoint).ConfigureAwait(false);
            if (id == "deepinfra") documents["usage"] = await GetJson("https://api.deepinfra.com/payment/usage?from=current").ConfigureAwait(false);
            if (id == "commandcode")
            {
                var org = Text(Get(documents["main"], "org"), "id");
                var query = org is null ? "" : "?orgId=" + Uri.EscapeDataString(org);
                documents["credits"] = await GetJson("https://api.commandcode.ai/alpha/billing/credits" + query).ConfigureAwait(false);
                documents["subscription"] = await GetJson("https://api.commandcode.ai/alpha/billing/subscriptions" + query).ConfigureAwait(false);
                var subscription = Get(documents["subscription"], "data");
                if (subscription.ValueKind != JsonValueKind.Object) subscription = documents["subscription"];
                if (Date(Get(subscription, "currentPeriodStart")) is { } since) query += (query.Length == 0 ? "?" : "&") + "since=" + Uri.EscapeDataString(since.ToString("O", CultureInfo.InvariantCulture));
                documents["usage"] = await GetJson("https://api.commandcode.ai/alpha/usage/summary" + query).ConfigureAwait(false);
            }
            var partial = false;
            if (id == "codebuff")
            {
                try { documents["subscription"] = await GetJson("https://www.codebuff.com/api/user/subscription").ConfigureAwait(false); }
                catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException) { token.ThrowIfCancellationRequested(); partial = true; }
            }
            var optional = id switch
            {
                "mimo" => new[] { ("detail", MiMoBase(setting) + "/tokenPlan/detail"), ("usage", MiMoBase(setting) + "/tokenPlan/usage") },
                "abacus" => [("billing", "https://apps.abacus.ai/api/_getBillingInfo")],
                "stepfun" => [("status", "https://platform.stepfun.com/api/step.openapi.devcenter.Dashboard/GetStepPlanStatus")],
                "sakana" => [("payg", "https://console.sakana.ai/billing?tab=payAsYouGo")],
                _ => Array.Empty<(string, string)>()
            };
            foreach (var (key, url) in optional)
            {
                try { documents[key] = await GetJson(url).ConfigureAwait(false); }
                catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
                { token.ThrowIfCancellationRequested(); partial = true; }
            }
            var reading = Parse(id, documents);
            return partial && reading.Windows.Count > 0 ? reading with { State = ReadingState.Partial, Message = "Primary usage is current. Additional billing details could not be refreshed." } : reading;
        }
        catch (ProviderRequestException error)
        {
            var state = error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? ReadingState.NeedsAuth
                : error.Status == HttpStatusCode.TooManyRequests ? ReadingState.Unavailable : ReadingState.Error;
            return new(id, state, [], Message: state == ReadingState.NeedsAuth ? "Sign in again or update the provider credential." : "Unable to refresh provider usage. The last reading is retained.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or System.Text.RegularExpressions.RegexMatchTimeoutException or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); return new(id, ReadingState.Error, [], Message: "Unable to refresh provider usage. Check the connection."); }
    }
    public static ProviderReading Parse(string id, IReadOnlyDictionary<string, JsonElement> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        if (ManagementIds.Contains(id)) return ParseManagement(id, payloads);
        if (BrowserIds.Contains(id)) return ParseBrowser(id, payloads);
        var root = payloads.GetValueOrDefault("main");
        if (id == "kimi") return ParseKimi(root);
        if (id == "amp") return ParseAmp(root);
        var windows = new List<LimitWindow>(); string? plan = null;
        void Percent(string key, string name, double? used, DateTimeOffset? reset = null, int minutes = 0)
        { if (used is >= 0 && double.IsFinite(used.Value)) windows.Add(new(key, name, used, reset, minutes)); }
        void Amount(string key, string name, double? value, string unit = "USD")
        { if (value is >= 0 && double.IsFinite(value.Value)) windows.Add(new(key, name, Unit: unit, DisplayValue: value.Value.ToString("N2", CultureInfo.CurrentCulture) + " " + unit)); }
        switch (id)
        {
            case "opencode":
                foreach (var (key, name, minutes) in new[] { ("rolling", "5h limit", 300), ("weekly", "Weekly limit", 10080), ("monthly", "Monthly limit", 0) })
                {
                    var value = Get(Get(root, "usage"), key); var reset = Date(Get(value, "resetsAt"));
                    Percent(key, name, Number(value, "percent"), reset, minutes != 0 ? minutes : reset.HasValue ? (int)(reset.Value - reset.Value.AddMonths(-1)).TotalMinutes : 0);
                }
                break;
            case "ollama":
                foreach (var key in new[] { "monthly", "weekly", "session" })
                {
                    var value = Get(Get(root, "limits"), key);
                    Percent(key, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key) + " usage", Number(value, "usage") * 100);
                    var models = Get(value, "models");
                    if (models.ValueKind == JsonValueKind.Array)
                        foreach (var model in models.EnumerateArray())
                            if (Text(model, "name") is { } name && Count(model, "request_count") is { } count)
                                windows.Add(new(key + "." + name, name, UsedCount: count, Unit: "requests"));
                }
                break;
            case "grok":
                var config = Get(root, "config"); var period = Get(config, "currentPeriod");
                var end = Date(Get(period, "end")) ?? Date(Get(config, "billingPeriodEnd"));
                var start = Date(Get(period, "start")) ?? Date(Get(config, "billingPeriodStart"));
                var duration = start.HasValue && end.HasValue ? Math.Max(0, (int)(end.Value - start.Value).TotalMinutes) : 0;
                Percent("credits", "Grok Build", Number(config, "creditUsagePercent"), end, duration);
                var products = Get(config, "productUsage");
                if (windows.Count == 0 && products.ValueKind == JsonValueKind.Array)
                    foreach (var product in products.EnumerateArray())
                    {
                        var name = Text(product, "product") ?? "Usage";
                        Percent(windows.Count == 0 ? "credits" : name, name, Number(product, "usagePercent"), end, duration);
                    }
                if (windows.Count == 0 && Text(period, "type")?.Contains("WEEKLY", StringComparison.Ordinal) == true) Percent("credits", "Weekly limit", 0, end, duration);
                break;
            case "cursor":
                var usage = Get(root, "individualUsage"); var included = Get(usage, "plan"); var resetAt = Date(Get(root, "billingCycleEnd"));
                var begins = Date(Get(root, "billingCycleStart")); var length = begins.HasValue && resetAt.HasValue ? Math.Max(0, (int)(resetAt.Value - begins.Value).TotalMinutes) : 0;
                Percent("auto", "Auto usage", Number(included, "autoPercentUsed"), resetAt, length);
                if (Number(included, "apiPercentUsed") is > 0) Percent("api", "API usage", Number(included, "apiPercentUsed"), resetAt, length);
                foreach (var (key, name, bucket) in new[] { ("on_demand", "On demand", Get(usage, "onDemand")), ("included", "Included usage", Get(usage, "overall")), ("team_on_demand", "Team on demand", Get(Get(root, "teamUsage"), "onDemand")) })
                    if ((key != "included" || windows.Count == 0) && Get(bucket, "enabled").ValueKind == JsonValueKind.True && Number(bucket, "limit") is > 0 and var limit)
                        Percent(key, name, Number(bucket, "used") / limit * 100, resetAt, length);
                plan = Text(root, "membershipType"); break;
            case "commandcode":
                var credits = payloads.GetValueOrDefault("credits"); var summary = payloads.GetValueOrDefault("usage");
                var subscription = Get(payloads.GetValueOrDefault("subscription"), "data");
                if (subscription.ValueKind != JsonValueKind.Object) subscription = payloads.GetValueOrDefault("subscription");
                var used = Number(summary, "totalCost"); var remaining = Number(Get(credits, "credits"), "monthlyCredits");
                if (used + remaining is > 0) Percent("monthly", "Monthly limit", used / (used + remaining) * 100, Date(Get(subscription, "currentPeriodEnd")));
                foreach (var (key, name) in new[] { ("fiveHour", "5h limit"), ("weekly", "Weekly limit") })
                {
                    var value = Get(Get(credits, "windowLimits"), key);
                    if (Number(value, "cap") is > 0 and var cap) Percent(key, name, (Number(value, "used") ?? 0) / cap * 100, FlexibleDate(Get(value, "resetAt")));
                }
                plan = Text(subscription, "planId"); break;
            case "fireworks":
                string? currency = null; double total = 0;
                var rows = Get(root, "lineItems");
                if (rows.ValueKind == JsonValueKind.Array)
                    foreach (var row in rows.EnumerateArray())
                    {
                        var cost = Get(row, "totalCost");
                        var units = Number(cost, "units");
                        if (units is null && double.TryParse(Text(cost, "units"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUnits)) units = parsedUnits;
                        if (units is null || Number(cost, "nanos") is not { } nanos || Text(cost, "currencyCode") is not { Length: > 0 } code) continue;
                        currency ??= code;
                        if (code == currency) total += units.Value + nanos / 1e9;
                    }
                if (currency is not null) Amount("spend", "Last 30 days", total, currency); break;
            case "deepinfra":
                var recent = Number(root, "recent"); var balance = Number(root, "stripe_balance");
                if (recent.HasValue && balance.HasValue) { Amount("balance", "Available balance", Math.Max(0, -(balance.Value + recent.Value))); Amount("owed", "Amount owed", Math.Max(0, balance.Value + recent.Value)); }
                var months = Get(payloads.GetValueOrDefault("usage"), "months");
                if (months.ValueKind == JsonValueKind.Array && months.GetArrayLength() > 0) Amount("month", "Current month spend", Number(months.EnumerateArray().Last(), "total_cost") / 100);
                break;
            case "codebuff":
                var consumed = Numeric(root, "usage") ?? Numeric(root, "used"); var ceiling = Numeric(root, "quota") ?? Numeric(root, "limit");
                var left = Numeric(root, "remainingBalance") ?? Numeric(root, "remaining");
                if (ceiling is > 0) Percent("credits", "Credits", consumed / ceiling * 100, FlexibleDate(Get(root, "next_quota_reset")));
                Amount("remaining", "Credits remaining", left, "credits");
                var subRoot = payloads.GetValueOrDefault("subscription"); var quota = Get(subRoot, "rateLimit");
                if ((Numeric(quota, "weeklyLimit") ?? Numeric(quota, "limit")) is > 0 and var weeklyLimit)
                    Percent("weekly", "Weekly limit", (Numeric(quota, "weeklyUsed") ?? Numeric(quota, "used")) / weeklyLimit * 100, FlexibleDate(Get(quota, "weeklyResetsAt")), 10080);
                plan = Text(Get(subRoot, "subscription"), "displayName") ?? Text(subRoot, "displayName"); break;
            case "neuralwatt":
                var subscriptionQuota = Get(root, "subscription");
                var includedKwh = Number(subscriptionQuota, "kwh_included");
                var usedKwh = Number(subscriptionQuota, "kwh_used"); var remainingKwh = Number(subscriptionQuota, "kwh_remaining");
                var totalKwh = includedKwh is > 0 ? includedKwh : usedKwh is >= 0 && remainingKwh is >= 0 ? usedKwh + remainingKwh : null;
                if (usedKwh is null && totalKwh is > 0 && remainingKwh is >= 0) usedKwh = Math.Max(0, totalKwh.Value - remainingKwh.Value);
                if (totalKwh is > 0 && usedKwh is >= 0)
                {
                    var startAt = Date(Get(subscriptionQuota, "current_period_start")); var endsAt = Date(Get(subscriptionQuota, "current_period_end"));
                    windows.Add(new("subscription", "Subscription energy", usedKwh / totalKwh * 100, endsAt,
                        startAt.HasValue && endsAt > startAt ? (int)(endsAt.Value - startAt.Value).TotalMinutes : 0,
                        Unit: "kWh", DisplayValue: $"{usedKwh:N2} / {totalKwh:N2} kWh"));
                }
                var allowance = Get(Get(root, "key"), "allowance");
                if (Get(allowance, "blocked").ValueKind == JsonValueKind.True) Percent("key", "Key allowance", 100);
                else if (Number(allowance, "limit_usd") is > 0 and var allowanceLimit)
                    Percent("key", "Key allowance", Number(allowance, "spent_usd") / allowanceLimit * 100);
                var pool = Get(root, "balance"); var remainingCredits = Number(pool, "credits_remaining_usd");
                if (remainingCredits is null && Number(pool, "total_credits_usd") is >= 0 and var totalCredits && Number(pool, "credits_used_usd") is >= 0 and var usedCredits)
                    remainingCredits = Math.Max(0, totalCredits - usedCredits);
                Amount("balance", "Prepaid balance", remainingCredits); Amount("month", "Current month cost", Number(Get(Get(root, "usage"), "current_month"), "cost_usd"));
                plan = Text(subscriptionQuota, "plan"); break;
        }
        return new(id, windows.Count > 0 ? ReadingState.Ready : ReadingState.Unavailable, windows.DistinctBy(x => x.Id).ToArray(), DateTimeOffset.Now,
            windows.Count > 0 ? null : "No metered usage was returned for this account.", plan);
    }
    private static double? Numeric(JsonElement root, string key) => Number(root, key) ??
        (double.TryParse(Text(root, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null);
    private static DateTimeOffset? FlexibleDate(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
        ? number <= 0 ? null : Date(value, number > 1e12) : Date(value);
    private sealed class ProviderRequestException(HttpStatusCode status) : Exception { internal HttpStatusCode Status { get; } = status; }
}
