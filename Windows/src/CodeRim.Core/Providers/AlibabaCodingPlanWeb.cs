using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Providers;

public sealed partial class NativeProviders
{
    public async Task<ProviderReading> FetchAlibabaCodingWebAsync(string? credential, string region,
        Func<Uri, string?>? cookieForUri = null, CancellationToken token = default)
    {
        const string id = "alibaba";
        var selected = AlibabaCodingPlanAuthentication.Region(region);
        if (selected is null) return new(id, ReadingState.Error, [], Message: "Choose an International or China Coding Plan region.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            token.ThrowIfCancellationRequested();
            var uris = new[] { selected.Dashboard, selected.UserInfo, selected.Quota };
            var headers = uris.ToDictionary(uri => uri.AbsoluteUri, uri => cookieForUri is null ? credential : cookieForUri(uri), StringComparer.Ordinal);
            var rpcCookies = AlibabaCodingSession(headers[selected.Quota.AbsoluteUri]);
            var scope = "alibaba:web:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { selected.Name, headers }))));
            foreach (var expired in retryAfter.Where(x => x.Value <= DateTimeOffset.UtcNow)) retryAfter.TryRemove(expired.Key, out _);
            if (retryAfter.TryGetValue(scope, out var retry) && retry > DateTimeOffset.UtcNow) throw new ProviderRequestException(HttpStatusCode.TooManyRequests);
            long totalBytes = 0;
            async Task<T> Bounded<T>(Task<T> operation)
            {
                try { return await operation.WaitAsync(budget.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    _ = operation.ContinueWith(done => { if (done.IsCompletedSuccessfully && done.Result is IDisposable resource) resource.Dispose(); else _ = done.Exception; },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    throw;
                }
            }
            Dictionary<string, string> ForRequest(Uri uri)
            {
                var value = AlibabaCodingSession(headers[uri.AbsoluteUri]);
                if (value["login_aliyunid_ticket"] != rpcCookies["login_aliyunid_ticket"]
                    || !AlibabaCodingAccountKeys.Any(key => value.TryGetValue(key, out var account) && rpcCookies.TryGetValue(key, out var expected) && account == expected)
                    || AlibabaCodingAccountKeys.Any(key => value.TryGetValue(key, out var account) && rpcCookies.TryGetValue(key, out var expected) && account != expected))
                    throw new ProviderRequestException(HttpStatusCode.Unauthorized);
                return value;
            }
            async Task<string> Request(Uri uri, string? sec = null)
            {
                budget.Token.ThrowIfCancellationRequested(); var cookie = ForRequest(uri);
                using var request = new HttpRequestMessage(sec is null ? HttpMethod.Get : HttpMethod.Post, uri);
                request.Headers.TryAddWithoutValidation("Cookie", AlibabaCodingPlanAuthentication.Header(cookie));
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/143.0.0.0 Safari/537.36");
                if (sec is null)
                {
                    request.Headers.Accept.ParseAdd(uri == selected.UserInfo ? "application/json, text/plain, */*" : "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                    if (uri == selected.UserInfo) request.Headers.Referrer = new Uri(selected.Origin + "/");
                }
                else
                {
                    var cornerstone = new Dictionary<string, object> { ["feTraceId"] = Guid.NewGuid().ToString(), ["feURL"] = selected.Dashboard.AbsoluteUri,
                        ["protocol"] = "V2", ["console"] = "ONE_CONSOLE", ["productCode"] = "p_efm", ["domain"] = new Uri(selected.Origin).Host,
                        ["consoleSite"] = selected.Site, ["userNickName"] = "", ["userPrincipalName"] = "", ["xsp_lang"] = "en-US" };
                    if (cookie.TryGetValue("cna", out var anonymous)) cornerstone["X-Anonymous-Id"] = anonymous;
                    var parameters = JsonSerializer.Serialize(new { Api = AlibabaCodingPlanAuthentication.QuotaApi, V = "1.0",
                        Data = new { queryCodingPlanInstanceInfoRequest = new { commodityCode = selected.Commodity, onlyLatestOne = true }, cornerstoneParam = cornerstone } });
                    request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["params"] = parameters, ["region"] = selected.Region, ["sec_token"] = sec });
                    request.Headers.Accept.ParseAdd("*/*"); request.Headers.Add("X-Requested-With", "XMLHttpRequest");
                    request.Headers.Add("Origin", selected.Origin); request.Headers.Referrer = new Uri(selected.Dashboard.GetLeftPart(UriPartial.Query));
                    var csrf = cookie.GetValueOrDefault("login_aliyunid_csrf") ?? cookie.GetValueOrDefault("csrf");
                    if (!string.IsNullOrEmpty(csrf)) { request.Headers.TryAddWithoutValidation("x-xsrf-token", csrf); request.Headers.TryAddWithoutValidation("x-csrf-token", csrf); }
                }
                using var response = await Bounded(client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMinutes(1);
                    retryAfter[scope] = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(wait.TotalSeconds, 1, 300));
                }
                if (!response.IsSuccessStatusCode) throw new ProviderRequestException(response.StatusCode);
                if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException();
                using var input = await Bounded(response.Content.ReadAsStreamAsync(budget.Token)).ConfigureAwait(false);
                using var output = new MemoryStream(); var buffer = new byte[16384];
                while (true)
                {
                    var count = await Bounded(input.ReadAsync(buffer, budget.Token).AsTask()).ConfigureAwait(false);
                    if (count == 0) break;
                    totalBytes += count;
                    if (output.Length + count > 2 * 1024 * 1024 || totalBytes > 6 * 1024 * 1024) throw new InvalidDataException();
                    output.Write(buffer, 0, count);
                }
                budget.Token.ThrowIfCancellationRequested();
                return new UTF8Encoding(false, true).GetString(output.ToArray());
            }
            string? sec = null; var transient = false;
            if (!string.IsNullOrWhiteSpace(headers[selected.Dashboard.AbsoluteUri]))
            {
                try { sec = AlibabaCodingPlanAuthentication.HtmlToken(await Request(selected.Dashboard).ConfigureAwait(false)); }
                catch (ProviderRequestException failure) when ((int)failure.Status >= 500 || failure.Status == HttpStatusCode.NotFound) { transient = true; }
            }
            if (sec is null && !string.IsNullOrWhiteSpace(headers[selected.UserInfo.AbsoluteUri]))
            {
                try
                {
                    using var json = JsonDocument.Parse(await Request(selected.UserInfo).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 32 });
                    sec = AlibabaCodingPlanAuthentication.JsonToken(json.RootElement);
                }
                catch (ProviderRequestException failure) when ((int)failure.Status >= 500 || failure.Status == HttpStatusCode.NotFound) { transient = true; }
            }
            sec ??= AlibabaCodingPlanAuthentication.SecurityToken(rpcCookies.GetValueOrDefault("sec_token"));
            if (sec is null)
            {
                if (transient) throw new IOException("Coding Plan discovery unavailable.");
                throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            }
            using var payload = JsonDocument.Parse(await Request(selected.Quota, sec).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 32 });
            var reading = AlibabaCodingPlanUsage.Parse(payload.RootElement, web: true);
            token.ThrowIfCancellationRequested(); return reading;
        }
        catch (ProviderRequestException failure)
        {
            token.ThrowIfCancellationRequested();
            var state = failure.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? ReadingState.NeedsAuth
                : failure.Status == HttpStatusCode.TooManyRequests ? ReadingState.Unavailable : ReadingState.Error;
            return new(id, state, [], Message: state == ReadingState.NeedsAuth ? "Reconnect the selected Coding Plan Web session." : "Coding Plan Web usage is temporarily unavailable.");
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or HttpRequestException or JsonException or DecoderFallbackException
            or System.Text.RegularExpressions.RegexMatchTimeoutException or OverflowException or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); return new(id, ReadingState.Error, [], Message: "Coding Plan Web usage could not be read. Check the selected session and retry."); }
    }
    private static readonly string[] AlibabaCodingAccountKeys = ["login_aliyunid_pk", "login_current_pk", "login_aliyunid"];
    private static Dictionary<string, string> AlibabaCodingSession(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        var cookies = AlibabaCodingPlanAuthentication.Cookies(raw);
        if (string.IsNullOrEmpty(cookies.GetValueOrDefault("login_aliyunid_ticket")) || !AlibabaCodingAccountKeys.Any(key => !string.IsNullOrEmpty(cookies.GetValueOrDefault(key))))
            throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        return cookies;
    }
}
