using System.Net;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Providers;

public sealed record StepFunFetchResult(ProviderReading Reading, string? Token);
public sealed partial class NativeProviders
{
    private const string StepFunOrigin = "https://platform.stepfun.com";
    private const string StepFunPassport = StepFunOrigin + "/passport/proto.api.passport.v1.PassportService/";
    public static readonly Uri StepFunUsageUri = new(StepFunOrigin + "/api/step.openapi.devcenter.Dashboard/QueryStepPlanRateLimit");
    public async Task<StepFunFetchResult> FetchStepFunAsync(StepFunCredential credential, Func<Uri, string?>? cookieForUri = null, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(40));
        var current = credential.Token;
        var browserToken = cookieForUri is null ? null : StepFunAuthentication.Token(cookieForUri(StepFunUsageUri));
        var scope = ProviderRetryScope.Create("stepfun", StepFunAuthentication.Scope(credential), StepFunOrigin, null);
        async Task<(JsonElement Body, string? Ingress)> Request(string url, string? body, string? auth, string? ingress = null, bool optional = false)
        {
            if (retryAfter.TryGetValue(scope, out var until) && until > DateTimeOffset.UtcNow) throw new ProviderRequestException(HttpStatusCode.TooManyRequests);
            if (cookieForUri is not null && (browserToken is null || StepFunAuthentication.Token(cookieForUri(new Uri(url))) != browserToken))
                throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(optional ? 2 : 15));
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var webid = StepFunWebId(auth ?? "");
            request.Headers.Add("oasis-appid", "10300"); request.Headers.Add("oasis-platform", "web");
            request.Headers.Add("oasis-webid", webid);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/147.0.0.0 Safari/537.36");
            var cookies = new List<string>();
            if (auth is not null) { cookies.Add("Oasis-Token=" + auth); cookies.Add("Oasis-Webid=" + webid); request.Headers.Add("Oasis-Token", auth); }
            if (ingress is not null) cookies.Add("INGRESSCOOKIE=" + ingress);
            if (cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", cookies));
            var pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            HttpResponseMessage response;
            try { response = await pending.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                _ = pending.ContinueWith(done => { if (done.IsCompletedSuccessfully) done.Result.Dispose(); else _ = done.Exception; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            using var responseLifetime = response;
            if (response.StatusCode == HttpStatusCode.TooManyRequests && !optional)
                retryAfter[scope] = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            if (!response.IsSuccessStatusCode) throw new ProviderRequestException(response.StatusCode);
            if (body is null)
            {
                var found = response.Headers.TryGetValues("Set-Cookie", out var headers)
                    ? headers.Select(value => value.Split(';')[0].Trim()).Where(value => value.StartsWith("INGRESSCOOKIE=", StringComparison.Ordinal))
                        .Select(value => StepFunAuthentication.Token(value["INGRESSCOOKIE=".Length..])).ToArray() : [];
                if (found.Length != 1 || found[0] is null) throw new InvalidDataException();
                return (default, found[0]);
            }
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException();
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream(); var buffer = new byte[16384]; int size;
            while ((size = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            { if (bytes.Length + size > 2 * 1024 * 1024) throw new InvalidDataException(); bytes.Write(buffer, 0, size); }
            using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            KimiUnique(json.RootElement);
            return (json.RootElement.Clone(), null);
        }
        async Task<string> Login()
        {
            if (StepFunAuthentication.Login(credential.Username, credential.Password) is null) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            var ingress = (await Request(StepFunOrigin, null, null).ConfigureAwait(false)).Ingress;
            var anonymous = StepFunAuthentication.CombinedToken((await Request(StepFunPassport + "RegisterDevice", "{}", null, ingress).ConfigureAwait(false)).Body);
            return StepFunAuthentication.CombinedToken((await Request(StepFunPassport + "SignInByPassword",
                JsonSerializer.Serialize(new { username = credential.Username, password = credential.Password }), anonymous, ingress).ConfigureAwait(false)).Body);
        }
        async Task<ProviderReading> Usage()
        {
            var main = (await Request(StepFunUsageUri.AbsoluteUri, "{}", current).ConfigureAwait(false)).Body;
            if (StepFunAuthFailure(main)) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            var payloads = new Dictionary<string, JsonElement> { ["main"] = main };
            var primary = ParseBrowser("stepfun", payloads);
            if (primary.State is not (ReadingState.Ready or ReadingState.Partial)) return primary;
            try { payloads["status"] = (await Request(StepFunOrigin + "/api/step.openapi.devcenter.Dashboard/GetStepPlanStatus", "{}", current, optional: true).ConfigureAwait(false)).Body; }
            catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
            { token.ThrowIfCancellationRequested(); return primary with { State = ReadingState.Partial }; }
            return ParseBrowser("stepfun", payloads);
        }
        try
        {
            if (credential.Kind is not ("manual" or "login") || current is not null && StepFunAuthentication.Token(current) != current)
                return new(new("stepfun", ReadingState.NeedsAuth, [], Message: "Update the selected StepFun credentials."), null);
            if (current is null)
            {
                if (credential.Kind != "login") throw new ProviderRequestException(HttpStatusCode.Unauthorized);
                current = await Login().ConfigureAwait(false);
            }
            try { return new(await Usage().ConfigureAwait(false), current); }
            catch (ProviderRequestException error) when (error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { }
            try
            {
                current = StepFunAuthentication.CombinedToken((await Request(StepFunPassport + "RefreshToken", "{}", current).ConfigureAwait(false)).Body);
                return new(await Usage().ConfigureAwait(false), current);
            }
            catch (ProviderRequestException error) when (error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                if (credential.Kind != "login") throw;
                current = await Login().ConfigureAwait(false);
                return new(await Usage().ConfigureAwait(false), current);
            }
        }
        catch (ProviderRequestException error)
        {
            var auth = error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            return new(new("stepfun", auth ? ReadingState.NeedsAuth : error.Status == HttpStatusCode.TooManyRequests ? ReadingState.Unavailable : ReadingState.Error, [],
                Message: auth ? "Sign in to StepFun again or replace the selected Oasis-Token." : "StepFun usage could not be refreshed."), current);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException or FormatException)
        { token.ThrowIfCancellationRequested(); return new(new("stepfun", ReadingState.Error, [], Message: "StepFun usage could not be refreshed."), current); }
    }
    private static bool StepFunAuthFailure(JsonElement root)
    {
        if (Numeric(root, "status") == 1) return false;
        if (Numeric(root, "code") is 401 or 403) return true;
        var message = ((ProviderParsers.Text(root, "message") ?? "") + " " + (ProviderParsers.Text(root, "desc") ?? "")).ToLowerInvariant();
        return message.Contains("unauthorized", StringComparison.Ordinal) || message.Contains("unauthenticated", StringComparison.Ordinal)
            || message.Contains("invalid token", StringComparison.Ordinal) || message.Contains("invalid credentials", StringComparison.Ordinal)
            || message.Contains("token expired", StringComparison.Ordinal) || message.Contains("expired token", StringComparison.Ordinal);
    }
}
