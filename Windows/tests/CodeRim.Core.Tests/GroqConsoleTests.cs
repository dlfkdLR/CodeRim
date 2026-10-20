using System.Net;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;

public sealed class GroqConsoleTests
{
    private static string Jwt(string organization = "org_test", bool fallback = false)
    {
        var payload = fallback ? JsonSerializer.Serialize(new Dictionary<string, object> { ["https://stytch.com/organization"] = new { slug = organization } })
            : JsonSerializer.Serialize(new Dictionary<string, object> { ["https://groq.com/organization"] = new { id = organization } });
        return "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
    }
    private static string Activity(object? cost = null, object? input = null) => JsonSerializer.Serialize(new { data = new[] {
        new { timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(), model = "test-model", n_context_tokens_total = input ?? 100,
            n_non_cached_context_tokens_total = 60, n_generated_tokens_total = 20, num_requests = 2, cost = cost ?? 0.125 } } });
    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectConsoleJwtUsesPinnedOrganizationEndpointAndPreservesUnits(bool fallback)
    {
        var jwt = Jwt("org_test", fallback);
        using var provider = new NativeProviders(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("api.groq.com", request.RequestUri!.Host);
            Assert.Equal("/platform/v1/organizations/org_test/activity", request.RequestUri.AbsolutePath);
            Assert.Contains("start_date=", request.RequestUri.Query); Assert.Contains("&end_date=", request.RequestUri.Query);
            Assert.Equal("Bearer " + jwt, request.Headers.Authorization!.ToString()); Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(Ok(Activity()));
        }));
        var reading = await provider.FetchAsync("groq", jwt, _ => "https://unrelated.invalid", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal("Groq console", reading.Plan);
        var entry = Assert.Single(reading.CostUsage!.Entries);
        Assert.Equal(100, entry.InputTokens); Assert.Equal(20, entry.OutputTokens); Assert.Null(entry.ReasoningTokens);
        Assert.Equal(2, entry.Requests); Assert.Equal(0.125, entry.Cost); Assert.Null(reading.Headline!.UsedPercent);
    }
    [Theory]
    [InlineData("../other")]
    [InlineData("org/other")]
    [InlineData("org?query")]
    [InlineData("org#fragment")]
    [InlineData("")]
    public async Task InvalidRoutingClaimDoesNotSendAnyCredential(string org)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(Ok("{}")); }));
        var reading = await provider.FetchAsync("groq", Jwt(org), _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, reading.State); Assert.Equal(0, calls);
    }
    [Fact]
    public async Task RefreshExchangesOnlySelectedSessionAndUsesReturnedJwt()
    {
        var fresh = Jwt("org_fresh"); var calls = 0;
        using var provider = new NativeProviders(new Handler(async (request, token) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("https://api.stytchb2b.groq.com/sdk/v1/b2b/sessions/authenticate", request.RequestUri!.AbsoluteUri);
                Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!));
                Assert.EndsWith(":opaque-fixture", pair); Assert.StartsWith("public-token-live-", pair);
                Assert.Equal("https://console.groq.com", Assert.Single(request.Headers.GetValues("Origin")));
                Assert.False(request.Headers.Contains("Cookie"));
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("opaque-fixture", body.RootElement.GetProperty("session_token").GetString());
                Assert.Equal(30, body.RootElement.GetProperty("session_duration_minutes").GetInt32());
                return Ok(JsonSerializer.Serialize(new { data = new { session_jwt = fresh } }));
            }
            Assert.Equal("Bearer " + fresh, request.Headers.Authorization!.ToString()); return Ok(Activity());
        }));
        var input = NativeProviders.GroqEnvironmentCredential(k => k == "GROQ_SESSION_TOKEN" ? "opaque-fixture" : null);
        var result = await provider.FetchAsync("groq", input, _ => "https://unrelated.invalid", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, result.State); Assert.Equal(2, calls);
    }
    [Theory]
    [InlineData(401, ReadingState.NeedsAuth)]
    [InlineData(403, ReadingState.NeedsAuth)]
    [InlineData(503, ReadingState.Error)]
    [InlineData(429, ReadingState.Unavailable)]
    public async Task RefreshErrorsWithoutFallbackRemainClassified(int status, ReadingState expected)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private error fixture") }); }));
        var result = await provider.FetchAsync("groq", "Session opaque-fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State); Assert.Equal(1, calls); Assert.DoesNotContain("private error", result.Message);
    }
    [Fact]
    public async Task FailedRefreshMayUseOnlyDirectJwtFromSameImportedProfile()
    {
        var jwt = Jwt(); var calls = 0;
        using var provider = new NativeProviders(new Handler((request, _) =>
        {
            calls++; return Task.FromResult(calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : request.Headers.Authorization!.Parameter == jwt ? Ok(Activity()) : throw new InvalidOperationException());
        }));
        var result = await provider.FetchAsync("groq", null, _ => "other-account", uri =>
        {
            Assert.Equal("https://console.groq.com/", uri.AbsoluteUri);
            return "stytch_session=opaque; stytch_session_jwt=" + jwt + "; unrelated=not-sent";
        }, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, result.State); Assert.Equal(2, calls);
    }
    [Fact]
    public async Task RefreshRequestTimeoutCanUseTheSameProfileDirectJwt()
    {
        var jwt = Jwt(); var calls = 0;
        using var provider = new NativeProviders(new Handler((request, _) =>
        {
            calls++;
            if (calls == 1) throw new TaskCanceledException("HTTP timeout fixture", new TimeoutException());
            Assert.Equal(jwt, request.Headers.Authorization!.Parameter); return Task.FromResult(Ok(Activity()));
        }));
        var input = JsonSerializer.Serialize(new { session_token = "opaque", session_jwt = jwt });
        var result = await provider.FetchAsync("groq", input, _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, result.State); Assert.Equal(2, calls);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PastedQuotedCookieHeaderIsNormalized(bool prefix)
    {
        var jwt = Jwt(); var calls = 0;
        using var provider = new NativeProviders(new Handler((request, _) =>
        {
            calls++; Assert.Equal(jwt, request.Headers.Authorization!.Parameter); return Task.FromResult(Ok(Activity()));
        }));
        var input = (prefix ? "Cookie: " : "") + (char)34 + "stytch_session_jwt=" + jwt + (char)34;
        var result = await provider.FetchAsync("groq", input, _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, result.State); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task CancellationDoesNotFallBackOrMakeAnActivityRequest()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) => { calls++; cancel.Cancel(); throw new HttpRequestException("cancelled fixture"); }));
        var input = JsonSerializer.Serialize(new { session_token = "opaque", session_jwt = Jwt() });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchAsync("groq", input, _ => null, cancel.Token));
        Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task ConsoleAuthFailureNeverFallsBackToEnterpriseMetrics(int status)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)); }));
        var result = await provider.FetchAsync("groq", Jwt(), _ => "enterprise-fixture", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, result.State); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task MissingCostStaysUnknownAndPartial()
    {
        var payload = JsonSerializer.Serialize(new { data = new[] { new { timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(), model = "model", n_context_tokens_total = 100, n_generated_tokens_total = 20, num_requests = 1 } } });
        using var provider = new NativeProviders(new Handler((_, _) => Task.FromResult(Ok(payload))));
        var result = await provider.FetchAsync("groq", Jwt(), _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Partial, result.State); Assert.Null(Assert.Single(result.CostUsage!.Entries).Cost);
        Assert.DoesNotContain(result.Windows, w => w.Id == "spend"); Assert.Equal("requests", result.Headline!.Id);
    }
    [Fact]
    public async Task AuthenticatedEmptyActivityIsAValidEmptyHistory()
    {
        using var provider = new NativeProviders(new Handler((_, _) => Task.FromResult(Ok("""{"data":[]}"""))));
        var result = await provider.FetchAsync("groq", Jwt(), _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, result.State); Assert.Empty(result.CostUsage!.Entries); Assert.Equal("0.00 USD", result.Headline!.DisplayValue);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(0.5)]
    [InlineData(1e16)]
    public async Task MalformedCountsCannotBecomeZero(double value)
    {
        using var provider = new NativeProviders(new Handler((_, _) => Task.FromResult(Ok(Activity(input: value)))));
        var result = await provider.FetchAsync("groq", Jwt(), _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, result.State); Assert.Null(result.CostUsage);
    }
    [Fact]
    public async Task AmbiguousBrowserSessionsAreRejectedBeforeRefresh()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(Ok("{}")); }));
        var result = await provider.FetchAsync("groq", null, _ => null, _ => "stytch_session=a; stytch_session=b", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, result.State); Assert.Equal(0, calls);
    }
    [Fact]
    public async Task OutOfWindowRowsDoNotBecomeConfirmedZeroUsage()
    {
        var json = JsonSerializer.Serialize(new { data = new[] { new { timestamp = DateTimeOffset.Now.AddDays(40).ToUnixTimeSeconds(), cost = 12, num_requests = 1, n_context_tokens_total = 1, n_generated_tokens_total = 1 } } });
        using var provider = new NativeProviders(new Handler((_, _) => Task.FromResult(Ok(json))));
        var result = await provider.FetchAsync("groq", Jwt(), _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, result.State); Assert.Null(result.CostUsage);
    }
    [Fact]
    public async Task OversizedConsoleResponseIsRejected()
    {
        using var provider = new NativeProviders(new Handler((_, _) => Task.FromResult(Ok(new string('x', 2 * 1024 * 1024 + 1)))));
        var result = await provider.FetchAsync("groq", Jwt(), _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, result.State); Assert.Null(result.CostUsage);
    }
    [Fact]
    public async Task NegativeCostDoesNotBecomeFreeUsage()
    {
        using var provider = new NativeProviders(new Handler((_, _) => Task.FromResult(Ok(Activity(cost: -0.1)))));
        var result = await provider.FetchAsync("groq", Jwt(), _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, result.State); Assert.Null(result.CostUsage);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\u0022data\u0022:{}}")]
    public async Task IncompleteRefreshWithoutDirectSessionCannotClaimAuthentication(string response)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(Ok(response)); }));
        var result = await provider.FetchAsync("groq", "Session opaque", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, result.State); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task RefreshedSessionUsesTheSameBoundedWhitespaceNormalization()
    {
        var jwt = Jwt(); var calls = 0;
        using var provider = new NativeProviders(new Handler((request, _) =>
        {
            calls++;
            if (calls == 1) return Task.FromResult(Ok(JsonSerializer.Serialize(new { data = new { session_jwt = " " + jwt + (char)13 + (char)10 } })));
            Assert.Equal(jwt, request.Headers.Authorization!.Parameter); return Task.FromResult(Ok(Activity()));
        }));
        var result = await provider.FetchAsync("groq", "Session opaque", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, result.State); Assert.Equal(2, calls);
    }
    [Fact]
    public async Task EmptySessionMarkerCannotBeUsedAsAnEnterpriseKey()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(Ok("{}")); }));
        var result = await provider.FetchAsync("groq", "Session   ", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, result.State); Assert.Equal(0, calls);
    }
    [Fact]
    public void EnvironmentSessionIsExplicitAndNeverIncludesEnterpriseKey()
    {
        Assert.Null(NativeProviders.GroqEnvironmentCredential(k => k == "GROQ_API_KEY" ? "enterprise" : null));
        using var input = JsonDocument.Parse(NativeProviders.GroqEnvironmentCredential(k => k == "GROQ_SESSION_TOKEN" ? " opaque " : k == "GROQ_SESSION_JWT" ? " jwt " : "enterprise")!);
        Assert.Equal("opaque", input.RootElement.GetProperty("session_token").GetString());
        Assert.Equal("jwt", input.RootElement.GetProperty("session_jwt").GetString());
    }
}
