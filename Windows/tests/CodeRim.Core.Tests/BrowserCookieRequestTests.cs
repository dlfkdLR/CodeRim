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
