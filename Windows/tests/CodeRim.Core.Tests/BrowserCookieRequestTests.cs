using System.Net;
using System.Text;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class BrowserCookieRequestTests
{
    private const string Quota = """{"totalQuota":{"quotaSummary":{"usedValue":2,"limitValue":10,"usagePercentage":20}}}""";
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QoderChinaCookiesNeverGoToGlobalAndAuthFailureCanFallBack(bool rejectedGlobal)
    {
        var jar = new BrowserCookieJar(rejectedGlobal ? [
            new("session", "china", ".qoder.com.cn", "/", true, false, 0),
            new("session", "global", ".qoder.com", "/", true, false, 0)
        ] : [new("session", "china", ".qoder.com.cn", "/", true, false, 0)], ["qoder.com", "qoder.com.cn"]);
        var calls = new List<string>();
        using var provider = new ScriptProviders(new Handler(request =>
        {
            var host = request.RequestUri!.Host; calls.Add(host);
            var cookie = string.Join("; ", request.Headers.GetValues("Cookie"));
            Assert.Equal(host == "qoder.com.cn" ? "session=china" : "session=global", cookie);
            return new HttpResponseMessage(host == "qoder.com.cn" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized) {
                Content = new StringContent(host == "qoder.com.cn" ? Quota : "", Encoding.UTF8, "application/json") };
        }));
        var reading = await provider.FetchAsync("qoder", _ => null, null, uri => jar.Header(uri, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal(rejectedGlobal ? ["qoder.com", "qoder.com.cn"] : ["qoder.com.cn"], calls);
        Assert.Equal(20, reading.Windows.Single().UsedPercent);
    }
    [Fact]
    public async Task ExpiredImportedCookieProducesAuthenticationStateWithoutNetwork()
    {
        var jar = new BrowserCookieJar([new("session", "expired", ".qoder.com.cn", "/", true, false, 1)], ["qoder.com", "qoder.com.cn"]);
        using var provider = new ScriptProviders(new Handler(_ => throw new InvalidOperationException("Expired cookie sent")));
        var reading = await provider.FetchAsync("qoder", _ => null, null, uri => jar.Header(uri, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, reading.State);
    }

    [Fact]
    public async Task PathScopedQoderCookieReachesItsApi()
    {
        var jar = new BrowserCookieJar([new("session", "path-only", ".qoder.com.cn", "/api", true, false, 0)], ["qoder.com", "qoder.com.cn"]);
        var calls = 0;
        using var provider = new ScriptProviders(new Handler(request => {
            calls++; Assert.Equal("qoder.com.cn", request.RequestUri!.Host);
            Assert.Equal("session=path-only", request.Headers.GetValues("Cookie").Single());
            return new(HttpStatusCode.OK) { Content = new StringContent(Quota) };
        }));
        var reading = await provider.FetchAsync("qoder", _ => null, null, uri => jar.Header(uri, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegionalServiceFailureIsNotMisreportedAsSignInFailure(bool rejectedGlobal)
    {
        var jar = new BrowserCookieJar(rejectedGlobal ? [new("session", "valid", ".qoder.com.cn", "/", true, false, 0), new("session", "invalid", ".qoder.com", "/", true, false, 0)] : [new("session", "valid", ".qoder.com.cn", "/", true, false, 0)], ["qoder.com", "qoder.com.cn"]);
        using var provider = new ScriptProviders(new Handler(request => new(request.RequestUri!.Host == "qoder.com" ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }));
        var reading = await provider.FetchAsync("qoder", _ => null, null, uri => jar.Header(uri, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State);
    }

    [Fact]
    public async Task MistralUsesConsoleScopedCsrfForVibe()
    {
        var jar = new BrowserCookieJar([
            new("session", "billing", "admin.mistral.ai", "/api", true, true, 0),
            new("ory_session_fixture", "console", "console.mistral.ai", "/api-ui", true, true, 0),
            new("csrftoken", "csrf", "console.mistral.ai", "/api-ui", true, true, 0)
        ], ["mistral.ai"]);
        var vibeRequests = 0;
        using var provider = new NativeProviders(new Handler(request => {
            string body;
            if (request.RequestUri!.Host == "console.mistral.ai") {
                vibeRequests++;
                Assert.Equal("csrf", request.Headers.GetValues("X-CSRFToken").Single());
                Assert.DoesNotContain("billing", request.Headers.GetValues("Cookie").Single(), StringComparison.Ordinal);
                body = """[{"result":{"data":{"json":{"usagePercentage":25}}}}]""";
            } else {
                Assert.Equal("session=billing", request.Headers.GetValues("Cookie").Single());
                body = """{"currency":"USD","completion":{"models":{}}}""";
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var reading = await provider.FetchAsync("mistral", null, _ => null, uri => jar.Header(uri, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        Assert.Equal(1, vibeRequests); Assert.Contains(reading.Windows, window => window.Id == "vibe" && window.UsedPercent == 25);
    }

    [Theory]
    [InlineData("mimo", "platform.xiaomimimo.com")]
    [InlineData("abacus", "apps.abacus.ai")]
    [InlineData("longcat", "longcat.chat")]
    public async Task AdditionalConsoleReadersKeepImportedCookiesScoped(string id, string host)
    {
        var cookies = id == "mimo"
            ? new BrowserCookie[] { new("api-platform_serviceToken", "fixture", host, "/api", true, true, 0), new("userId", "fixture-user", host, "/api", true, true, 0) }
            : [new("session", "fixture", host, "/api", true, true, 0)];
        var jar = new BrowserCookieJar([..cookies, new("private", "wrong-path", host, "/account", true, true, 0)], [host]);
        var calls = 0;
        using var provider = new NativeProviders(new Handler(request => {
            calls++;
            Assert.Equal(host, request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            var cookie = request.Headers.GetValues("Cookie").Single();
            Assert.DoesNotContain("private", cookie, StringComparison.Ordinal);
            if (id == "mimo") {
                Assert.Contains("api-platform_serviceToken=fixture", cookie);
                Assert.Contains("userId=fixture-user", cookie);
            } else Assert.Equal("session=fixture", cookie);
            var body = id switch {
                "mimo" => """{"code":0,"data":{"balance":10,"currency":"USD"}}""",
                "abacus" => """{"success":true,"result":{"totalComputePoints":100,"computePointsLeft":67}}""",
                _ => request.RequestUri.AbsolutePath switch {
                    "/api/v1/user-current" => """{"code":0,"data":{"name":"Fixture"}}""",
                    "/api/pay/quota/metering/token-packs/summary" => """{"code":0,"data":{"currentLot":{"status":"ACTIVE","totalToken":100,"consumedToken":33}}}""",
                    _ => """{"code":0,"data":{"totalQuota":100,"list":[{"availableToken":67}]}}"""
                }
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var reading = await provider.FetchAsync(id, null, _ => null,
            uri => jar.Header(uri, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.NotEmpty(reading.Windows); Assert.True(calls >= 2);
        if (id != "mimo") Assert.Equal(33, reading.Headline!.UsedPercent);
        Assert.Null(jar.Header(new Uri("https://" + host + ".evil.example/api"), DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("qwencloud", "", "home.qwencloud.com", "cs-data.qwencloud.com", "/billing/subscription/token-plan-individual", true)]
    [InlineData("qwencloud", "", "home.qwencloud.com", "cs-data.qwencloud.com", "/billing/subscription/token-plan-individual", false)]
    [InlineData("alibabatokenplan", "cn-personal", "bailian.console.aliyun.com", "bailian-cs.console.aliyun.com", "/cn-beijing/", true)]
    [InlineData("alibabatokenplan", "intl-personal", "modelstudio.console.alibabacloud.com", "bailian-singapore-cs.alibabacloud.com", "/ap-southeast-1/", true)]
    public async Task TokenPlanImportedSecTokenUsesOnlyPairedConsoleOrigins(string id, string region, string dashboardHost, string apiHost, string dashboardPath, bool dashboardSec)
    {
        var jar = new BrowserCookieJar([
            new("session", "dashboard-only", dashboardHost, dashboardPath, true, true, 0),
            new("session", "api-only", apiHost, "/data", true, true, 0),
            new("csrf", "api-csrf", apiHost, "/data", true, true, 0),
            new("sec_token", "scoped-sec", dashboardSec ? dashboardHost : apiHost, dashboardSec ? dashboardPath : "/data", true, true, 0),
            new("sec_token", "wrong-scope", dashboardHost, "/unrelated", true, true, 0)
        ], [dashboardHost, apiHost]);
        var apiCalls = 0;
        using var provider = new NativeProviders(new AsyncHandler(async request => {
            if (request.RequestUri!.AbsolutePath != "/data/api.json")
                return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath == "/tool/user/info.json" ? "{}" : "<html></html>") };
            apiCalls++;
            Assert.Equal(apiHost, request.RequestUri.Host);
            var cookie = request.Headers.GetValues("Cookie").Single();
            Assert.Contains("session=api-only", cookie);
            Assert.DoesNotContain("dashboard-only", cookie, StringComparison.Ordinal);
            Assert.DoesNotContain("wrong-scope", cookie, StringComparison.Ordinal);
            if (dashboardSec) Assert.DoesNotContain("sec_token=", cookie, StringComparison.Ordinal);
            var form = (await request.Content!.ReadAsStringAsync()).Split('&').Select(x => x.Split('=', 2))
                .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1].Replace('+', ' ')));
            Assert.Equal("scoped-sec", form["sec_token"]);
            Assert.Equal("api-csrf", request.Headers.GetValues("x-csrf-token").Single());
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"per5HourPercentage":0.33}""") };
        }));
        var reading = await provider.FetchAsync(id, null, key => key == "ALIBABA_TOKEN_PLAN_REGION" ? region : null,
            uri => jar.Header(uri, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal(33, reading.Headline!.UsedPercent); Assert.Equal(3, apiCalls);
    }

    [Fact]
    public async Task QwenDoesNotBorrowSecTokenFromUnrelatedCookiePath()
    {
        var jar = new BrowserCookieJar([
            new("session", "dashboard", "home.qwencloud.com", "/", true, true, 0),
            new("session", "api", "cs-data.qwencloud.com", "/", true, true, 0),
            new("sec_token", "unrelated", "home.qwencloud.com", "/unrelated", true, true, 0)
        ], ["qwencloud.com"]);
        using var provider = new NativeProviders(new Handler(request => {
            Assert.NotEqual("/data/api.json", request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath == "/tool/user/info.json" ? "{}" : "<html></html>") };
        }));
        var reading = await provider.FetchAsync("qwencloud", null, _ => null,
            uri => jar.Header(uri, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, reading.State);
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    [Fact]
    public async Task ManusHostOnlySessionBecomesBearerWithoutLeakingCookieToApi()
    {
        var jar = new BrowserCookieJar([new("session_id", "manus-login", "manus.im", "/", true, true, 0)], ["manus.im"]);
        var calls = 0;
        using var provider = new ScriptProviders(new Handler(request => {
            calls++; Assert.Equal("api.manus.im", request.RequestUri!.Host);
            Assert.Equal("Bearer manus-login", request.Headers.Authorization!.ToString());
            Assert.False(request.Headers.Contains("Cookie"));
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"totalCredits":100,"proMonthlyCredits":200,"periodicCredits":100}""") };
        }));
        var reading = await provider.FetchAsync("manus", _ => null, null, uri => jar.Header(uri, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.Equal(ReadingState.Ready, reading.State);
    }
}
