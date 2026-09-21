using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

public sealed partial class NativeProviders
{
    private static readonly TimeSpan MiniMaxRegexBudget = TimeSpan.FromMilliseconds(100);
    private static Match MiniMaxMatch(string text, string pattern) => Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, MiniMaxRegexBudget);
    private static string MiniMaxReplace(string text, string pattern, string replacement) => Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, MiniMaxRegexBudget);
    private static JsonElement MiniMaxJson(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        KimiUnique(document.RootElement);
        var root = document.RootElement;
        foreach (var context in Contexts(root))
        {
            var envelope = Get(context, "base_resp"); if (envelope.ValueKind != JsonValueKind.Object) envelope = Get(context, "baseResp");
            var status = Numeric(envelope, "status_code");
            var message = Text(envelope, "status_msg");
            if (status == 1004 || message is not null && (message.Contains("not login", StringComparison.OrdinalIgnoreCase)
                || message.Contains("login expired", StringComparison.OrdinalIgnoreCase) || message.Contains("cookie", StringComparison.OrdinalIgnoreCase) || message.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)))
                throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            if (status.HasValue && status != 0) throw new InvalidDataException();
        }
        return root.Clone();
    }
    public static ProviderReading ParseMiniMaxWebPlan(string text, DateTimeOffset? clock = null)
    {
        if (Encoding.UTF8.GetByteCount(text) > 2 * 1024 * 1024) throw new InvalidDataException();
        var now = clock ?? DateTimeOffset.Now;
        ProviderReading? Structured(JsonElement root)
        {
            foreach (var context in Contexts(root))
            {
                var models = Get(context, "model_remains");
                if (models.ValueKind != JsonValueKind.Array) models = Get(context, "modelRemains");
                if (models.ValueKind != JsonValueKind.Array && Get(context, "services").ValueKind != JsonValueKind.Array) continue;
                var normalized = JsonSerializer.SerializeToElement(new {
                    model_remains = models.ValueKind == JsonValueKind.Array ? models : JsonSerializer.SerializeToElement(Array.Empty<object>()),
                    services = Get(context, "services").ValueKind == JsonValueKind.Array ? Get(context, "services") : JsonSerializer.SerializeToElement(Array.Empty<object>()),
                    current_subscribe_title = Text(context, "current_subscribe_title") ?? Text(context, "currentSubscribeTitle") ?? Text(context, "plan_name") ?? Text(context, "planName") ?? Text(context, "combo_title") ?? Text(context, "comboTitle")
                        ?? Text(context, "current_plan_title") ?? Text(context, "currentPlanTitle") ?? Text(Get(context, "current_combo_card"), "title"),
                    points_balance = Numeric(context, "points_balance") ?? Numeric(context, "pointsBalance") ?? Numeric(context, "credits_balance") ?? Numeric(context, "creditsBalance")
                });
                var reading = ParseSubscription("minimax", normalized);
                if (reading.Windows.Any(window => window.Id != "points")) return reading;
            }
            return null;
        }
        if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
            return Structured(MiniMaxJson(text)) ?? throw new InvalidDataException();
        var visible = MiniMaxReplace(text, @"<!--.*?-->|<script\b[^>]*>.*?</script\s*>|<style\b[^>]*>.*?</style\s*>", " ");
        visible = WebUtility.HtmlDecode(MiniMaxReplace(visible, "<[^>]+>", " "));
        if (MiniMaxMatch(visible, @"\b(?:sign\s*in|log\s*in)\b|登录|登入").Success)
            throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        var next = MiniMaxMatch(text, @"<script\b[^>]*\bid\s*=\s*[""']__NEXT_DATA__[""'][^>]*>(.*?)</script\s*>");
        if (next.Success && Structured(MiniMaxJson(next.Groups[1].Value)) is { } structured) return structured;
        var used = MiniMaxMatch(visible, @"(\d+(?:\.\d+)?)\s*%\s*(?:used|已使用)");
        if (!used.Success) used = MiniMaxMatch(visible, @"used\s*(\d+(?:\.\d+)?)\s*%");
        var available = MiniMaxMatch(visible, @"available\s+usage[:\s]*([\d,]+)\s*prompts?\s*/\s*(\d+(?:\.\d+)?)\s*(hours?|hrs?|h|minutes?|mins?|m|days?|d)\b");
        if (!used.Success || !double.TryParse(used.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || percent is < 0 or > 100)
            throw new InvalidDataException();
        var plan = MiniMaxMatch(visible, @"(?:Coding|Token)\s*Plan\s*(?:Starter|Plus|Max|Pro|Standard|Premium|Enterprise)\b");
        var minutes = available.Success ? MiniMaxDuration(available.Groups[2].Value, available.Groups[3].Value) : 0;
        DateTimeOffset? reset = null;
        var relative = MiniMaxMatch(visible, @"resets?\s+in\s+(\d+)\s*(seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h|days?|d)\b");
        if (relative.Success && MiniMaxSeconds(relative.Groups[1].Value, relative.Groups[2].Value) is > 0 and <= 31536000 and var seconds) reset = now.AddSeconds(seconds);
        var at = MiniMaxMatch(visible, @"resets?\s+at\s+(\d{1,2}:\d{2})(?:\s*\(([^)]+)\))?");
        if (reset is null && at.Success && TimeOnly.TryParseExact(at.Groups[1].Value, MiniMaxClockFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            var zone = TimeZoneInfo.Local;
            try
            {
                if (at.Groups[2].Success)
                {
                    var zoneText = at.Groups[2].Value.Trim();
                    var offset = MiniMaxMatch(zoneText, @"^(?:UTC|GMT)([+-])(\d{1,2})(?::?(\d{2}))?$");
                    if (offset.Success && int.TryParse(offset.Groups[2].Value, out var offsetHours) && offsetHours <= 14
                        && (!offset.Groups[3].Success || int.TryParse(offset.Groups[3].Value, out var minutePart) && minutePart < 60))
                    {
                        var offsetMinutes = offset.Groups[3].Success ? int.Parse(offset.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                        var totalOffset = TimeSpan.FromMinutes((offsetHours * 60 + offsetMinutes) * (offset.Groups[1].Value == "-" ? -1 : 1));
                        if (Math.Abs(totalOffset.TotalHours) <= 14) zone = TimeZoneInfo.CreateCustomTimeZone("MiniMax reset", totalOffset, zoneText, zoneText);
                    }
                    else zone = TimeZoneInfo.FindSystemTimeZoneById(zoneText);
                }
                var local = TimeZoneInfo.ConvertTime(now, zone);
                var date = local.Date.Add(time.ToTimeSpan());
                if (date <= local.DateTime) date = date.AddDays(1);
                if (!zone.IsInvalidTime(date)) reset = new DateTimeOffset(date, zone.GetUtcOffset(date));
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return new("minimax", ReadingState.Ready, [new("web.coarse", "General", percent, reset, minutes)],
            now, Plan: plan.Success ? plan.Value : null);
    }
    private static double MiniMaxSeconds(string number, string unit)
    {
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var count) || !double.IsFinite(count) || count < 0) return 0;
        return count * (char.ToLowerInvariant(unit[0]) switch { 'd' => 86400, 'h' => 3600, 'm' => 60, _ => 1 });
    }
    private static int MiniMaxDuration(string number, string unit)
    {
        var minutes = MiniMaxSeconds(number, unit) / 60;
        return minutes is > 0 and < int.MaxValue ? (int)Math.Round(minutes) : 0;
    }

    private static string? MiniMaxSession(string cookie) => cookie.Split(';').Select(pair => pair.Trim())
        .FirstOrDefault(pair => pair.StartsWith("HERTZ-SESSION=", StringComparison.Ordinal))?["HERTZ-SESSION=".Length..];
    public async Task<ProviderReading> FetchMiniMaxWebAsync(MiniMaxWebCredential? credential, CancellationToken token = default)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(token); overall.CancelAfter(TimeSpan.FromSeconds(35));
        if (credential is { BrowserState: not null, Bearer: null } && KimiAuthentication.Clean(MiniMaxSession(credential.Cookie)) is { } session)
            credential = credential with { Bearer = session };
        try { return await FetchMiniMaxWebAttemptAsync(credential, true, overall.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); return new("minimax", ReadingState.Error, [], Message: "MiniMax Web refresh timed out."); }
    }
    private async Task<ProviderReading> FetchMiniMaxWebAttemptAsync(MiniMaxWebCredential? credential, bool browserFallback, CancellationToken token)
    {
        if (credential is null || MiniMaxAuthentication.Parse(credential.Cookie, credential.Region) is null
            || MiniMaxAuthentication.Parse(credential.Cookie, credential.Region)?.Group is { } cookieGroup && cookieGroup != credential.Group
            || credential.BrowserState is not null && credential.Bearer is not null && credential.Bearer != MiniMaxSession(credential.Cookie)
            || credential.Bearer is not null && KimiAuthentication.Clean(credential.Bearer) != credential.Bearer
            || credential.Group is not null && (credential.Group.Length is 0 or > 256 || credential.Group.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')))
            return new("minimax", ReadingState.NeedsAuth, [], Message: "Save a MiniMax Web session for the selected region.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(35));
        var domain = MiniMaxAuthentication.Domain(credential.Region); var platform = "https://platform." + domain;
        var planUri = MiniMaxAuthentication.PlanUri(credential.Region);
        var scope = ProviderRetryScope.Create("minimax", JsonSerializer.Serialize(credential.BrowserState is null ? credential : credential with { Bearer = null }), platform, "web");
        var totalBytes = 0;
        try
        {
            foreach (var expired in retryAfter.Where(pair => pair.Value <= DateTimeOffset.Now)) retryAfter.TryRemove(expired.Key, out _);
            if (retryAfter.TryGetValue(scope, out var retry) && retry > DateTimeOffset.Now) throw new ProviderRequestException(HttpStatusCode.TooManyRequests);
            var jar = credential.BrowserState is null ? null : BrowserCookieJar.Parse(credential.BrowserState, [domain]);
            async Task<string> Request(Uri uri, string kind, CancellationToken requestToken, bool optional = false)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                var cookie = jar is null ? credential.Cookie : jar.Header(uri, DateTimeOffset.UtcNow);
                if (string.IsNullOrEmpty(cookie)) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
                if (jar is not null)
                {
                    var scoped = MiniMaxAuthentication.Parse(cookie, credential.Region);
                    static string? Session(string header) => header.Split(';').Select(pair => pair.Trim()).FirstOrDefault(pair => pair.StartsWith("HERTZ-SESSION=", StringComparison.Ordinal));
                    if (scoped is null || scoped.Group is not null && scoped.Group != credential.Group
                        || Session(credential.Cookie) is { } session && Session(cookie) != session)
                        throw new ProviderRequestException(HttpStatusCode.Unauthorized);
                }
                request.Headers.Add("Cookie", cookie);
                // The browser bearer is only the selected HERTZ cookie; URI session/group checks above prevent cross-account enrichment.
                if (kind != "metadata" && credential.Bearer is not null)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Bearer);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/143.0.0.0 Safari/537.36");
                request.Headers.Accept.ParseAdd(kind == "plan" ? "text/html,application/xhtml+xml,application/json" : "application/json");
                request.Headers.Add("Origin", kind == "remains" ? uri.GetLeftPart(UriPartial.Authority) : platform);
                request.Headers.Referrer = new Uri(platform + (kind == "billing" ? "/account" : "/user-center/payment/coding-plan"));
                if (kind is "remains" or "billing") request.Headers.Add("x-requested-with", "XMLHttpRequest");
                if (kind == "metadata")
                {
                    request.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9");
                    if (credential.Group is not null) request.Headers.Add("x-group-id", credential.Group);
                }
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
                    retryAfter[scope] = response.Headers.RetryAfter?.Date ?? DateTimeOffset.Now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                if (!response.IsSuccessStatusCode) throw new ProviderRequestException(response.StatusCode);
                if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException();
                using var stream = await response.Content.ReadAsStreamAsync(requestToken).WaitAsync(requestToken).ConfigureAwait(false);
                using var memory = new MemoryStream(); var bytes = new byte[16384]; int count;
                while ((count = await stream.ReadAsync(bytes, requestToken).AsTask().WaitAsync(requestToken).ConfigureAwait(false)) > 0)
                {
                    totalBytes += count;
                    if (memory.Length + count > 2 * 1024 * 1024 || totalBytes > 8 * 1024 * 1024) throw new InvalidDataException();
                    memory.Write(bytes, 0, count);
                }
                requestToken.ThrowIfCancellationRequested();
                return new UTF8Encoding(false, true).GetString(memory.ToArray());
            }
            ProviderReading? reading = null; var partial = false;
            try { reading = ParseMiniMaxWebPlan(await Request(planUri, "plan", deadline.Token).ConfigureAwait(false)); }
            catch (ProviderRequestException error) when (error.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) { }
            catch (Exception error) when (error is InvalidDataException or JsonException or HttpRequestException or IOException or RegexMatchTimeoutException) { }
            async Task<ProviderReading> Remains()
            {
                var suffix = "/v1/api/openplatform/coding_plan/remains" + (credential.Group is null ? "" : "?GroupId=" + Uri.EscapeDataString(credential.Group));
                try { return ParseMiniMaxWebPlan(await Request(new Uri(platform + suffix), "remains", deadline.Token).ConfigureAwait(false)); }
                catch (ProviderRequestException error) when (error.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) { }
                catch (Exception error) when (error is InvalidDataException or JsonException or HttpRequestException or IOException) { }
                return ParseMiniMaxWebPlan(await Request(new Uri("https://www." + domain + suffix), "remains", deadline.Token).ConfigureAwait(false));
            }
            try
            {
                if (reading is null || reading.Headline?.Id == "web.coarse")
                {
                var more = await Remains().ConfigureAwait(false);
                reading = more with { Plan = more.Plan ?? reading?.Plan };
                }
            }
            catch (ProviderRequestException error) when (reading is not null && error.Status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) { partial = true; }
            catch (Exception error) when (reading is not null && error is InvalidDataException or JsonException or HttpRequestException or IOException) { partial = true; }
            if (reading is null) throw new InvalidDataException();
            using var optional = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); optional.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                if (credential.Group is not null)
                {
                var metadata = MiniMaxJson(await Request(new Uri("https://www." + domain + "/v1/api/openplatform/charge/combo/cycle_audio_resource_package?biz_line=2&cycle_type=3&resource_package_type=7"), "metadata", optional.Token, true).ConfigureAwait(false));
                var current = Contexts(metadata).SelectMany(context => new[] { Get(context, "current_subscribe"), Get(context, "current_subscription"), Get(context, "current_plan") })
                    .FirstOrDefault(item => item.ValueKind == JsonValueKind.Object);
                var title = Text(current, "current_subscribe_title") ?? Text(current, "title") ?? Text(current, "name") ?? Text(current, "plan_name");
                if (title is { Length: > 0 and <= 256 } && !title.Any(char.IsControl)) reading = reading with { Plan = title };
                }
            }
            catch (Exception error) when (MiniMaxOptionalFailure(error)) { token.ThrowIfCancellationRequested(); partial = true; }
            var rows = new List<JsonElement>(); var historyComplete = false; var now = DateTimeOffset.Now; var today = now.Date; var start = today.AddDays(-29);
            var seenPages = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                for (var page = 1; page <= 10; page++)
                {
                    var billing = MiniMaxJson(await Request(new Uri(platform + "/account/amount?page=" + page.ToString(CultureInfo.InvariantCulture) + "&limit=100&aggregate=false"), "billing", optional.Token, true).ConfigureAwait(false));
                    var data = Get(billing, "data"); if (data.ValueKind != JsonValueKind.Object) data = billing;
                    var records = Get(data, "charge_records"); if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() > 100) throw new InvalidDataException();
                    var batch = records.EnumerateArray().ToArray();
                    if (batch.Length > 0 && !seenPages.Add(records.GetRawText())) throw new InvalidDataException();
                    rows.AddRange(batch);
                    var total = Numeric(data, "total_cnt");
                    if (batch.Length == 0) { historyComplete = total is null or 0 || rows.Count >= total; break; }
                    if (total is >= 0 && rows.Count >= total || batch.All(row => MiniMaxBillingDate(row) is { } date && date.LocalDateTime < start))
                    { historyComplete = true; break; }
                }
            }
            catch (ProviderRequestException error) when (credential.Bearer is not null && error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { throw; }
            catch (Exception error) when (MiniMaxOptionalFailure(error)) { token.ThrowIfCancellationRequested(); partial = true; }
            if (!historyComplete) partial = true;
            var validRows = rows.Where(row => MiniMaxBillingDate(row) is { } date && date.LocalDateTime >= start && date <= now && MiniMaxBillingSuccess(row)).ToArray();
            var windows = reading.Windows.ToList();
            foreach (var currentDay in new[] { true, false })
            {
                var selected = validRows.Where(row => !currentDay || MiniMaxBillingDate(row)?.LocalDateTime.Date == today).ToArray();
                decimal totalTokens = 0; double totalCash = 0; var knownTokens = false; var knownCash = false;
                foreach (var row in selected)
                {
                    var count = Numeric(row, "consume_token");
                    if (count is not > 0)
                    {
                        var input = Numeric(row, "consume_input_token"); var output = Numeric(row, "consume_output_token");
                        count = input.HasValue || output.HasValue ? Math.Max(0, (input ?? 0) + (output ?? 0)) : count;
                    }
                    if (count is >= 0 && count < long.MaxValue) { totalTokens += (decimal)count.Value; knownTokens = true; }
                    var cash = Numeric(row, "consume_cash_after_voucher") ?? Numeric(row, "consume_cash");
                    if (cash is { } value) { totalCash += value; knownCash = true; }
                }
                var tokens = knownTokens && totalTokens < long.MaxValue ? (long?)totalTokens : null;
                var parts = new List<string>();
                if (tokens is { } displayTokens) parts.Add(displayTokens.ToString("N0", CultureInfo.CurrentCulture) + " tokens");
                if (knownCash && double.IsFinite(totalCash)) parts.Add(totalCash.ToString("N2", CultureInfo.CurrentCulture) + " net charges (currency unspecified)");
                if (parts.Count > 0) windows.Add(new(currentDay ? "account.today" : "account.30days", currentDay ? "Today · Account" : "Last 30 days · Account",
                    UsedCount: tokens, Unit: tokens.HasValue ? "tokens" : null, DisplayValue: string.Join(" · ", parts) + (historyComplete ? "" : " (partial)")));
            }
            token.ThrowIfCancellationRequested();
            return reading with { Windows = windows, State = partial ? ReadingState.Partial : ReadingState.Ready,
                Message = partial ? "Quota is current. Some subscription or activity details were unavailable." : null };
        }
        catch (ProviderRequestException error)
        {
            var auth = error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            if (auth && browserFallback && credential.BrowserState is not null && credential.Bearer is not null)
                return await FetchMiniMaxWebAttemptAsync(credential with { Bearer = null }, false, token).ConfigureAwait(false);
            return new("minimax", auth ? ReadingState.NeedsAuth : error.Status == HttpStatusCode.TooManyRequests ? ReadingState.Unavailable : ReadingState.Error,
                [], Message: auth ? "Sign in to MiniMax in the selected region again." : "MiniMax Web usage could not be refreshed.");
        }
        catch (Exception error) when (MiniMaxOptionalFailure(error) || error is ArgumentException)
        {
            token.ThrowIfCancellationRequested();
            if (browserFallback && credential.BrowserState is not null && credential.Bearer is not null && error is InvalidDataException or JsonException)
                return await FetchMiniMaxWebAttemptAsync(credential with { Bearer = null }, false, token).ConfigureAwait(false);
            return new("minimax", ReadingState.Error, [], Message: "MiniMax Web usage could not be refreshed.");
        }
    }
    private static bool MiniMaxOptionalFailure(Exception error) => error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException
        or JsonException or OperationCanceledException or RegexMatchTimeoutException;
    private static bool MiniMaxBillingSuccess(JsonElement row)
    {
        var result = Get(row, "result"); if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) result = Get(row, "status");
        return result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || result.ValueKind == JsonValueKind.String && (string.IsNullOrWhiteSpace(result.GetString()) || result.GetString()!.Trim().Equals("SUCCESS", StringComparison.OrdinalIgnoreCase));
    }
    private static DateTimeOffset? MiniMaxBillingDate(JsonElement row)
    {
        if (Get(row, "created_at").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) return EpochDate(row, "created_at");
        var ymd = Text(row, "ymd");
        if (ymd is not null)
            return DateTime.TryParseExact(ymd, MiniMaxDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day)) : null;
        var time = Text(row, "consume_time");
        return DateTimeOffset.TryParseExact(time, MiniMaxTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }
    private static readonly string[] MiniMaxClockFormats = ["H:mm", "HH:mm"];
    private static readonly string[] MiniMaxDateFormats = ["yyyy-MM-dd", "yyyyMMdd", "yyyy/MM/dd"];
    private static readonly string[] MiniMaxTimeFormats = ["yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss'Z'"];
}
