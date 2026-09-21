using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

public sealed record KimiFetchResult(ProviderReading Reading, bool CanFallback);

public sealed partial class NativeProviders
{
    public static readonly Uri KimiWebUsageUri = new("https://www.kimi.com/apiv2/kimi.gateway.billing.v1.BillingService/GetUsages");
    private const string KimiMembership = "https://www.kimi.com/apiv2/kimi.gateway.membership.v2.MembershipService/";
    public async Task<ProviderReading> FetchKimiAsync(KimiCredential credential, CancellationToken token = default)
        => (await FetchKimiResultAsync(credential, token).ConfigureAwait(false)).Reading;
    public async Task<KimiFetchResult> FetchKimiResultAsync(KimiCredential credential, CancellationToken token = default)
    {
        var requestStarted = false;
        if (credential.Source is not ("api" or "cli" or "web")) return new(new("kimi", ReadingState.Error, [], Message: "Choose Auto, API or Web."), false);
        if (KimiAuthentication.Clean(credential.Token) is not { } selected)
            return new(new("kimi", ReadingState.NeedsAuth, [], Message: "Connect Kimi Code or save a Kimi Web session."), true);
        if (credential.Source == "cli" && (credential.ApiBaseUrl is not null || credential.ExpiresAt is not { } expiry
            || !double.IsFinite(expiry) || expiry <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + 60))
            return new(new("kimi", ReadingState.NeedsAuth, [], Message: "Sign in again with Kimi Code CLI."), true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var web = credential.Source == "web";
            var endpoint = web ? KimiWebUsageUri.AbsoluteUri : KimiEndpoint(_ => credential.ApiBaseUrl);
            var retryScope = ProviderRetryScope.Create("kimi", selected, endpoint, credential.Source);
            foreach (var expired in retryAfter.Where(pair => pair.Value <= DateTimeOffset.Now)) retryAfter.TryRemove(expired.Key, out _);
            if (retryAfter.TryGetValue(retryScope, out var retry) && retry > DateTimeOffset.Now) throw new ProviderRequestException(HttpStatusCode.TooManyRequests);
            var jar = credential.BrowserState is null ? null : BrowserCookieJar.Parse(credential.BrowserState, ["kimi.com"]);
            async Task<JsonElement> Request(string url, string? body, CancellationToken requestToken, bool optional = false)
            {
                var uri = new Uri(url);
                if (jar is not null && KimiAuthentication.WebToken(jar.Header(uri, DateTimeOffset.UtcNow)) != selected)
                    throw new ProviderRequestException(HttpStatusCode.Unauthorized);
                using var request = new HttpRequestMessage(web ? HttpMethod.Post : HttpMethod.Get, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", selected);
                request.Headers.Accept.ParseAdd(web ? "*/*" : "application/json");
                request.Headers.UserAgent.ParseAdd(web ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/143.0.0.0 Safari/537.36" : "CodeRim/2.1.7");
                if (web)
                {
                    request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
                    request.Headers.Add("Cookie", "kimi-auth=" + selected);
                    request.Headers.Add("Origin", "https://www.kimi.com"); request.Headers.Referrer = new Uri("https://www.kimi.com/code/console");
                    request.Headers.Add("Accept-Language", "en-US,en;q=0.9"); request.Headers.Add("connect-protocol-version", "1");
                    request.Headers.Add("x-language", "en-US"); request.Headers.Add("x-msh-platform", "web");
                    var zone = TimeZoneInfo.Local.Id;
                    if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone, out var iana)) zone = iana;
                    request.Headers.Add("r-timezone", zone);
                    KimiSessionHeaders(request, selected);
                }
                else if (credential.Source == "cli")
                {
                    request.Headers.Add("X-Msh-Platform", "kimi_code_cli"); request.Headers.Add("X-Msh-Version", "2.1.7");
                    static string Ascii(string text) => new(text.Where(c => c >= 0x20 && c <= 0x7e).Take(256).ToArray());
                    request.Headers.Add("X-Msh-Device-Name", Ascii(Environment.MachineName));
                    request.Headers.Add("X-Msh-Device-Model", Ascii(Environment.OSVersion + " " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture));
                    request.Headers.Add("X-Msh-Os-Version", Ascii(Environment.OSVersion.Version.ToString()));
                    if (credential.DeviceId is { Length: <= 256 } device && KimiAuthentication.Clean(device) is not null) request.Headers.Add("X-Msh-Device-Id", device);
                }
                requestStarted = true;
                var pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
                HttpResponseMessage response;
                try { response = await pending.WaitAsync(requestToken).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    _ = pending.ContinueWith(completed => { if (completed.IsCompletedSuccessfully) completed.Result.Dispose(); else _ = completed.Exception; },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    throw;
                }
                using var responseLifetime = response;
                if (response.StatusCode == HttpStatusCode.TooManyRequests && !optional)
                    retryAfter[retryScope] = response.Headers.RetryAfter?.Date ?? DateTimeOffset.Now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                if (!response.IsSuccessStatusCode) throw new ProviderRequestException(response.StatusCode);
                if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException();
                using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
                using var memory = new MemoryStream(); var bytes = new byte[16384]; int count;
                while ((count = await stream.ReadAsync(bytes, requestToken).ConfigureAwait(false)) > 0)
                { if (memory.Length + count > 2 * 1024 * 1024) throw new InvalidDataException(); memory.Write(bytes, 0, count); }
                requestToken.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                KimiUnique(document.RootElement);
                return document.RootElement.Clone();
            }
            var primary = await Request(endpoint, web ? """{"scope":["FEATURE_CODING"]}""" : null, deadline.Token).ConfigureAwait(false);
            if (!web) return new(ParseKimi(primary), true);
            var primaryOnly = ParseKimiWeb(primary);
            if (primaryOnly.State == ReadingState.Error) return new(primaryOnly, true);
            // Both optional requests share a deadline; a stalled title cannot erase completed statistics.
            using var optionalDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            optionalDeadline.CancelAfter(TimeSpan.FromSeconds(2));
            async Task<JsonElement> Optional(string name)
            {
                try
                {
                    var pending = Request(KimiMembership + name, "{}", optionalDeadline.Token, true);
                    try { return await pending.WaitAsync(optionalDeadline.Token).ConfigureAwait(false); }
                    finally
                    {
                        if (!pending.IsCompleted) _ = pending.ContinueWith(completed => { _ = completed.Exception; },
                            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
                catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
                { token.ThrowIfCancellationRequested(); return default; }
            }
            var statsTask = Optional("GetSubscriptionStats"); var planTask = Optional("GetSubscription");
            await Task.WhenAll(statsTask, planTask).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(ParseKimiWeb(primary, await statsTask.ConfigureAwait(false), await planTask.ConfigureAwait(false)), true);
        }
        catch (ProviderRequestException error)
        {
            var auth = error.Status == HttpStatusCode.Unauthorized || credential.Source == "web" && error.Status == HttpStatusCode.Forbidden;
            return new(new("kimi", auth ? ReadingState.NeedsAuth : error.Status == HttpStatusCode.TooManyRequests ? ReadingState.Unavailable : ReadingState.Error, [],
                Message: auth ? "Sign in to the selected Kimi connection again." : error.Status == HttpStatusCode.Forbidden
                    ? "Kimi denied this request's permission or quota." : "Kimi usage could not be refreshed."), error.Status != HttpStatusCode.BadRequest);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException or FormatException)
        { token.ThrowIfCancellationRequested(); return new(new("kimi", ReadingState.Error, [], Message: "Kimi usage could not be refreshed."), requestStarted); }
    }
    private static void KimiUnique(JsonElement root)
    {
        var nodes = 0;
        void Visit(JsonElement item)
        {
            if (++nodes > 16384) throw new InvalidDataException();
            if (item.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in item.EnumerateObject()) { if (!names.Add(pair.Name)) throw new InvalidDataException(); Visit(pair.Value); }
            }
            else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) Visit(child);
        }
        Visit(root);
    }
    private static void KimiSessionHeaders(HttpRequestMessage request, string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[1].Length > 8192) return;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/'); payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload), new JsonDocumentOptions { MaxDepth = 8 });
            KimiUnique(document.RootElement);
            foreach (var (field, header) in new[] { ("device_id", "x-msh-device-id"), ("ssid", "x-msh-session-id"), ("sub", "x-traffic-id") })
                if (Text(document.RootElement, field) is { Length: > 0 and <= 256 } value && KimiAuthentication.Clean(value) is not null) request.Headers.Add(header, value);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidDataException) { }
    }
    private static bool KimiStatsValid(JsonElement stats)
    {
        if (stats.ValueKind != JsonValueKind.Object) return false;
        static bool OptionalObject(JsonElement value) => value.ValueKind is JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null;
        static bool StringField(JsonElement value, string name) => Get(value, name).ValueKind is JsonValueKind.String or JsonValueKind.Undefined or JsonValueKind.Null;
        static bool NumberField(JsonElement value, string name) => Get(value, name).ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || Number(value, name) is not null;
        var balance = Get(stats, "subscriptionBalance"); var code = Get(stats, "ratelimitCode7d");
        return OptionalObject(balance) && OptionalObject(code)
            && StringField(balance, "feature") && StringField(balance, "type") && StringField(balance, "expireTime")
            && NumberField(balance, "amountUsedRatio") && NumberField(balance, "kimiCodeUsedRatio")
            && StringField(code, "resetTime") && NumberField(code, "ratio")
            && Get(code, "enabled").ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Undefined or JsonValueKind.Null;
    }
    public static ProviderReading ParseKimiWeb(JsonElement primary, JsonElement stats = default, JsonElement plan = default)
    {
        var usages = Get(primary, "usages");
        if (usages.ValueKind != JsonValueKind.Array) return new("kimi", ReadingState.Error, [], Message: "Kimi returned no coding quota.");
        var coding = usages.EnumerateArray().Where(item => Text(item, "scope") == "FEATURE_CODING").ToArray();
        if (coding.Length != 1) return new("kimi", ReadingState.Error, [], Message: "Kimi returned ambiguous coding quota.");
        if (Get(coding[0], "detail").ValueKind != JsonValueKind.Object || Get(coding[0], "limits").ValueKind is not (JsonValueKind.Array or JsonValueKind.Undefined or JsonValueKind.Null))
            return new("kimi", ReadingState.Error, [], Message: "Kimi returned invalid coding quota.");
        using var converted = JsonDocument.Parse(JsonSerializer.Serialize(new { usage = Get(coding[0], "detail"), limits =
            Get(coding[0], "limits").ValueKind == JsonValueKind.Array ? Get(coding[0], "limits") : JsonSerializer.SerializeToElement(Array.Empty<object>()) }));
        var reading = ParseKimi(converted.RootElement); var windows = reading.Windows.ToList();
        if (windows.Count == 0) return new("kimi", ReadingState.Error, [], Message: "Kimi returned no valid coding quota.");
        if (!KimiStatsValid(stats)) stats = default;
        var balance = Get(stats, "subscriptionBalance");
        if ((Text(balance, "feature") is null or "FEATURE_OMNI") && (Text(balance, "type") is null or "SUBSCRIPTION")
            && Number(balance, "amountUsedRatio") is { } ratio)
            windows.Add(new("kimi-monthly", "Total usage", Math.Clamp(ratio * 100, 0, 100), Date(Get(balance, "expireTime"))));
        var extra = Get(stats, "ratelimitCode7d");
        if (Get(extra, "enabled").ValueKind != JsonValueKind.False && Number(extra, "ratio") is { } codeRatio)
        {
            var code = new LimitWindow("kimi-code-7d", "Code 7-day", Math.Clamp(codeRatio * 100, 0, 100), Date(Get(extra, "resetTime")), 10080);
            var weekly = windows.FirstOrDefault(window => window.Id == "limit_7d");
            if (!(weekly is { DurationMinutes: > 0, UsedPercent: { } used, ResetsAt: { } reset }
                && code.ResetsAt is { } codeReset && Math.Abs(used - code.UsedPercent!.Value) <= 1 && Math.Abs((reset - codeReset).TotalSeconds) <= 300)) windows.Add(code);
        }
        var subscription = Get(plan, "subscription");
        var title = Get(subscription, "active").ValueKind == JsonValueKind.True && Text(subscription, "status") == "SUBSCRIPTION_STATUS_ACTIVE"
            ? Text(Get(subscription, "goods"), "title")?.Trim() : null;
        if (title is { Length: 0 or > 256 } || title?.Any(char.IsControl) == true) title = null;
        var partial = stats.ValueKind == JsonValueKind.Undefined || plan.ValueKind == JsonValueKind.Undefined;
        return reading with { Windows = windows, Plan = title, State = partial ? ReadingState.Partial : ReadingState.Ready,
            Message = partial ? "Coding quota is current. Some subscription details were unavailable." : null };
    }
}
