using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;

public sealed class SpecializedCooldownTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage Limited(bool expired = false)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = expired ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-1))
            : new RetryConditionHeaderValue(TimeSpan.FromHours(1));
        return response;
    }
    private static string Session(string account, string? org = null, string? client = null, string? access = null)
        => new FactoryWorkOsProfile(access, "refresh-" + account, org, client, null).Serialize();
    private static string Jwt(string org = "fixture_org") => "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new Dictionary<string, object> { ["https://groq.com/organization"] = new { id = org } })))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
    private static HttpResponseMessage FactoryQuota(HttpRequestMessage request)
        => Ok(request.RequestUri!.AbsolutePath == "/api/app/auth/me" ? """{"userProfile":{"id":"fixture_user"}}"""
            : """{"usesTokenRateLimitsBilling":true,"limits":{"standard":{"fiveHour":{"usedPercent":17}}}}""");
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply(request));
    }
    [Theory]
    [InlineData("factory")]
    [InlineData("amp")]
    [InlineData("groq")]
    public async Task RepeatedSessionHonorsCooldownAndOtherSessionStillReachesTransport(string id)
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(_ => { calls++; return Limited(); }));
        Task<ProviderReading> Fetch(string account) => id switch {
            "factory" => provider.FetchFactorySessionAsync(Session(account), _ => null, token: Token),
            "amp" => provider.FetchAmpBrowserAsync("session=" + account, token: Token),
            _ => provider.FetchAsync("groq", "Session " + account, _ => null, Token)
        };
        Assert.Equal(ReadingState.Unavailable, (await Fetch("a")).State); Assert.Equal(1, calls);
        Assert.Equal(ReadingState.Unavailable, (await Fetch("a")).State); Assert.Equal(1, calls);
        Assert.Equal(ReadingState.Unavailable, (await Fetch("b")).State); Assert.Equal(2, calls);
        Assert.Equal(ReadingState.Unavailable, (await Fetch("a")).State); Assert.Equal(2, calls);
    }
    [Theory]
    [InlineData("factory")]
    [InlineData("amp")]
    [InlineData("groq")]
    public async Task ExpiredRetryAfterAllowsTheSameSessionAgain(string id)
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(_ => { calls++; return Limited(expired: true); }));
        Task<ProviderReading> Fetch() => id switch {
            "factory" => provider.FetchFactorySessionAsync(Session("a"), _ => null, token: Token),
            "amp" => provider.FetchAmpBrowserAsync("session=a", token: Token),
            _ => provider.FetchAsync("groq", "Session a", _ => null, Token)
        };
        await Fetch(); await Fetch(); Assert.Equal(2, calls);
    }
    [Theory]
    [InlineData("factory")]
    [InlineData("amp")]
    [InlineData("groq")]
    public async Task CancellationIsObservedEvenWhileCoolingDown(string id)
    {
        var calls = 0; using var cancel = new CancellationTokenSource();
        using var provider = new NativeProviders(new Handler(_ => { calls++; return Limited(); }));
        Task<ProviderReading> Fetch(CancellationToken token) => id switch {
            "factory" => provider.FetchFactorySessionAsync(Session("a"), _ => null, token: token),
            "amp" => provider.FetchAmpBrowserAsync("session=a", token: token),
            _ => provider.FetchAsync("groq", "Session a", _ => null, token)
        };
        await Fetch(Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fetch(cancel.Token)); Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData("organization")]
    [InlineData("client")]
    public async Task FactoryRefreshCooldownIncludesTheSelectedDestination(string change)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler(_ => { calls++; return Limited(); }));
        var first = Session("a", "org_a", FactoryWorkOsProfile.ClientIds[0]);
        var second = Session("a", change == "organization" ? "org_b" : "org_a", FactoryWorkOsProfile.ClientIds[change == "client" ? 1 : 0]);
        await provider.FetchFactorySessionAsync(first, _ => null, token: Token);
        await provider.FetchFactorySessionAsync(first, _ => null, token: Token); Assert.Equal(1, calls);
        await provider.FetchFactorySessionAsync(second, _ => null, token: Token); Assert.Equal(2, calls);
    }
    [Fact]
    public async Task FactoryNormalizedProfileAliasesCannotBypassRefreshCooldown()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler(_ => { calls++; return Limited(); }));
        await provider.FetchFactorySessionAsync(Session("a", "org_a"), _ => null, token: Token);
        await provider.FetchFactorySessionAsync("""{"refreshToken":" refresh-a ","organizationId":" org_a ","unrelated":"different"}""", _ => null, token: Token);
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task FactoryRefreshLimitDoesNotBlockWorkingAccessQuota()
    {
        var posts = 0; var quota = 0;
        using var provider = new NativeProviders(new Handler(request => {
            if (request.RequestUri!.Host == "api.workos.com") { posts++; return Limited(); }
            quota++; return FactoryQuota(request);
        }));
        Assert.Equal(ReadingState.Unavailable, (await provider.FetchFactorySessionAsync(Session("a"), _ => null, token: Token)).State);
        Assert.Equal(ReadingState.Ready, (await provider.FetchFactorySessionAsync(Session("a", access: "working-access"), _ => null, token: Token)).State);
        Assert.Equal(1, posts); Assert.Equal(2, quota);
    }
    [Fact]
    public async Task FactoryQuotaLimitDoesNotRefreshAgainAndDoesNotBlockOtherAccess()
    {
        var posts = 0; var quota = 0;
        using var provider = new NativeProviders(new Handler(request => {
            if (request.RequestUri!.Host == "api.workos.com") { posts++; return Ok("""{"access_token":"fresh-a","refresh_token":"rotated-a"}"""); }
            quota++; return Limited();
        }));
        string? rotated = null;
        Assert.Equal(ReadingState.Unavailable, (await provider.FetchFactorySessionAsync(Session("a"), _ => null, text => { rotated = text; return true; }, Token)).State);
        Assert.NotNull(rotated); Assert.Equal(1, posts); Assert.Equal(1, quota);
        Assert.Equal(ReadingState.Unavailable, (await provider.FetchFactorySessionAsync(rotated, _ => null, token: Token)).State);
        Assert.Equal(1, posts); Assert.Equal(1, quota);
        await provider.FetchFactorySessionAsync(Session("b", access: "fresh-b"), _ => null, token: Token);
        Assert.Equal(1, posts); Assert.Equal(2, quota);
    }
    [Fact]
    public async Task AmpNormalizedSessionIgnoresUnsentCookiesAndRawFormatting()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler(_ => { calls++; return Limited(); }));
        await provider.FetchAmpBrowserAsync("session=a; analytics=first", token: Token);
        await provider.FetchAmpBrowserAsync("Cookie: ' analytics=second; session=a '", token: Token);
        await provider.FetchAmpBrowserAsync("a", token: Token); Assert.Equal(1, calls);
        await provider.FetchAmpBrowserAsync(null, _ => "session=b", Token); Assert.Equal(2, calls);
    }
    [Fact]
    public async Task AmpRedirectUsesActualPathScopedCookieForCooldown()
    {
        var rootCalls = 0; var detailCalls = 0; var selected = "a";
        using var provider = new NativeProviders(new Handler(request => {
            if (request.RequestUri!.AbsolutePath == "/settings") {
                rootCalls++; var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("/settings/usage", UriKind.Relative); return redirect;
            }
            detailCalls++; Assert.Equal("session=" + selected, Assert.Single(request.Headers.GetValues("Cookie"))); return Limited();
        }));
        Task<ProviderReading> Fetch() => provider.FetchAmpBrowserAsync(null,
            uri => uri.AbsolutePath == "/settings" ? "session=root" : "session=" + selected, Token);
        await Fetch(); await Fetch(); Assert.Equal(2, rootCalls); Assert.Equal(1, detailCalls);
        selected = "b"; await Fetch(); Assert.Equal(3, rootCalls); Assert.Equal(2, detailCalls);
        selected = "a"; await Fetch(); Assert.Equal(4, rootCalls); Assert.Equal(2, detailCalls);
    }
    [Fact]
    public async Task GroqNormalizedSessionFormatsShareRefreshCooldown()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler(_ => { calls++; return Limited(); }));
        await provider.FetchAsync("groq", "Session fixture", _ => null, Token);
        await provider.FetchAsync("groq", """{"session_token":" fixture "}""", _ => null, Token);
        await provider.FetchAsync("groq", null, _ => null, _ => "stytch_session=fixture; analytics=ignored", Token);
        Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroqQuotaLimitStopsTransactionBeforeAnyFurtherRefresh(bool refresh)
    {
        var posts = 0; var quota = 0;
        using var provider = new NativeProviders(new Handler(request => {
            if (request.Method == HttpMethod.Post) { posts++; return Ok(JsonSerializer.Serialize(new { data = new { session_jwt = Jwt() } })); }
            quota++; return Limited();
        }));
        var profile = refresh ? JsonSerializer.Serialize(new { session_token = "selected-a" }) : Jwt();
        await provider.FetchAsync("groq", profile, _ => null, Token); await provider.FetchAsync("groq", profile, _ => null, Token);
        Assert.Equal(refresh ? 1 : 0, posts); Assert.Equal(1, quota);
        var other = refresh ? JsonSerializer.Serialize(new { session_token = "selected-b" }) : Jwt("other_org");
        await provider.FetchAsync("groq", other, _ => null, Token);
        Assert.Equal(refresh ? 2 : 0, posts); Assert.Equal(2, quota);
    }
    [Fact]
    public async Task GroqOptionalRefreshLimitStillAllowsSameProfileJwtQuotaWithoutRepeatedRefresh()
    {
        var posts = 0; var quota = 0;
        using var provider = new NativeProviders(new Handler(request => {
            if (request.Method == HttpMethod.Post) { posts++; return Limited(); }
            quota++; Assert.Equal(Jwt(), request.Headers.Authorization!.Parameter); return Ok("""{"data":[]}""");
        }));
        var input = JsonSerializer.Serialize(new { session_token = "selected", session_jwt = Jwt() });
        Assert.Equal(ReadingState.Ready, (await provider.FetchAsync("groq", input, _ => null, Token)).State);
        Assert.Equal(ReadingState.Ready, (await provider.FetchAsync("groq", input, _ => null, Token)).State);
        Assert.Equal(1, posts); Assert.Equal(2, quota);
    }
    [Fact]
    public async Task GroqOptionalRefreshAndQuotaLimitsAreBothHonored()
    {
        var posts = 0; var quota = 0;
        using var provider = new NativeProviders(new Handler(request => { if (request.Method == HttpMethod.Post) posts++; else quota++; return Limited(); }));
        var input = JsonSerializer.Serialize(new { session_token = "selected", session_jwt = Jwt() });
        Assert.Equal(ReadingState.Unavailable, (await provider.FetchAsync("groq", input, _ => null, Token)).State);
        Assert.Equal(ReadingState.Unavailable, (await provider.FetchAsync("groq", input, _ => null, Token)).State);
        Assert.Equal(1, posts); Assert.Equal(1, quota);
    }
    [Theory]
    [InlineData("factory")]
    [InlineData("amp")]
    [InlineData("groq")]
    public async Task NonRateLimitFailuresDoNotCreateCooldown(string id)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler(_ => { calls++; return new(HttpStatusCode.ServiceUnavailable); }));
        Task<ProviderReading> Fetch() => id switch {
            "factory" => provider.FetchFactorySessionAsync(Session("a"), _ => null, token: Token),
            "amp" => provider.FetchAmpBrowserAsync("session=a", token: Token),
            _ => provider.FetchAsync("groq", "Session a", _ => null, Token)
        };
        Assert.Equal(ReadingState.Error, (await Fetch()).State); Assert.Equal(ReadingState.Error, (await Fetch()).State); Assert.Equal(2, calls);
    }
}
