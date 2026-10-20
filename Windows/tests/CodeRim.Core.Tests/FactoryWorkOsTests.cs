using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;
public sealed class FactoryWorkOsTests
{
    private const string Limits = """{"usesTokenRateLimitsBilling":true,"limits":{"standard":{"fiveHour":{"usedPercent":17}}}}""";
    private const string Refreshed = """{"access_token":"fresh-access","refresh_token":"rotated-refresh","organization_id":"org_test"}""";
    private const string Profile = """{"refresh_token":"original-refresh","organization_id":"org_test"}""";
    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static HttpResponseMessage Quota(HttpRequestMessage request)
    {
        Assert.Equal("api.factory.ai", request.RequestUri!.Host);
        Assert.Equal("Bearer fresh-access", request.Headers.Authorization!.ToString()); Assert.False(request.Headers.Contains("Cookie"));
        return Ok(request.RequestUri.AbsolutePath == "/api/app/auth/me" ? """{"userProfile":{"id":"member"}}""" : Limits);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token); }
    [Fact]
    public async Task ExplicitProfileRefreshesOnlyAtPinnedEndpointAndCommitsRotationBeforeQuota()
    {
        var calls = 0; string? saved = null;
        using var provider = new NativeProviders(new Handler(async (r, token) =>
        {
            calls++;
            if (r.RequestUri!.Host != "api.workos.com") { Assert.NotNull(saved); return Quota(r); }
            Assert.Equal("/user_management/authenticate", r.RequestUri.AbsolutePath); Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Null(r.Headers.Authorization); Assert.False(r.Headers.Contains("Cookie"));
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(token));
            Assert.Equal("original-refresh", json.RootElement.GetProperty("refresh_token").GetString());
            Assert.Equal("org_test", json.RootElement.GetProperty("organization_id").GetString());
            Assert.Equal(FactoryWorkOsProfile.ClientIds[0], json.RootElement.GetProperty("client_id").GetString());
            Assert.Equal("refresh_token", json.RootElement.GetProperty("grant_type").GetString());
            return Ok(Refreshed);
        }));
        var reading = await provider.FetchFactorySessionAsync(Profile, _ => "https://unrelated.invalid", value => { saved = value; return true; }, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(17, reading.Headline!.UsedPercent); Assert.Equal(3, calls);
        var rotated = FactoryWorkOsProfile.Parse(saved!);
        Assert.Equal("rotated-refresh", rotated.RefreshToken); Assert.Equal("fresh-access", rotated.AccessToken); Assert.NotNull(rotated.RefreshedAt);
        Assert.Equal(FactoryWorkOsProfile.ClientIds[0], rotated.ClientId); Assert.DoesNotContain("refresh", rotated.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task GenericNativeRouteAcceptsFormattedSessionJson()
    {
        using var provider = new NativeProviders(new Handler((r, _) => Task.FromResult(r.RequestUri!.Host == "api.workos.com" ? Ok(Refreshed) : Quota(r))));
        var reading = await provider.FetchAsync("factory", "{\n \"refresh_token\": \"original-refresh\"\n}", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State);
    }
    [Fact]
    public async Task WorkingAccessDoesNotNeedRefreshOrWriteback()
    {
        using var provider = new NativeProviders(new Handler((r, _) => Task.FromResult(Quota(r))));
        var profile = """{"access_token":"fresh-access","refresh_token":"old-refresh"}""";
        var reading = await provider.FetchFactorySessionAsync(profile, _ => null, _ => throw new InvalidOperationException("Unexpected rotation"), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredOrRejectedAccessCanRefreshTheSameExplicitSession(bool expired)
    {
        var posts = 0; var oldCalls = 0;
        using var provider = new NativeProviders(new Handler((r, _) =>
        {
            if (r.RequestUri!.Host == "api.workos.com") { posts++; return Task.FromResult(Ok(Refreshed)); }
            if (r.Headers.Authorization?.Parameter != "fresh-access") { oldCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }
            return Task.FromResult(Quota(r));
        }));
        var old = expired ? "e30.eyJleHAiOjF9.signature" : "old-access";
        var reading = await provider.FetchFactorySessionAsync(JsonSerializer.Serialize(new { access_token = old, refresh_token = "same-profile" }), _ => null, token: TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(1, posts); Assert.Equal(expired ? 0 : 3, oldCalls);
    }
    [Fact]
    public async Task OnlyRejectedDefaultClientTriesTheSecondPinnedClient()
    {
        var clients = new List<string>();
        using var provider = new NativeProviders(new Handler(async (r, token) =>
        {
            if (r.RequestUri!.Host != "api.workos.com") return Quota(r);
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(token)); clients.Add(json.RootElement.GetProperty("client_id").GetString()!);
            return clients.Count == 1 ? new HttpResponseMessage(HttpStatusCode.BadRequest) : Ok(Refreshed);
        }));
        var reading = await provider.FetchFactorySessionAsync(Profile, _ => null, token: TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(FactoryWorkOsProfile.ClientIds, clients);
    }
    [Theory]
    [InlineData(429, ReadingState.Unavailable)]
    [InlineData(503, ReadingState.Error)]
    [InlineData(302, ReadingState.NeedsAuth)]
    public async Task RateLimitServiceFailureAndRedirectStopWithoutAnotherClient(int status, ReadingState state)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)); }));
        var reading = await provider.FetchFactorySessionAsync(Profile, _ => null, token: TestContext.Current.CancellationToken);
        Assert.Equal(state, reading.State); Assert.Equal(1, calls);
        if (status == 429)
        {
            Assert.Equal(ReadingState.Unavailable, (await provider.FetchFactorySessionAsync(Profile, _ => null, token: TestContext.Current.CancellationToken)).State);
            Assert.Equal(1, calls);
        }
    }
    [Fact]
    public async Task CallerCancellationStopsRefreshAndPersistence()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var saves = 0;
        using var provider = new NativeProviders(new Handler((_, _) => { cancel.Cancel(); return Task.FromResult(Ok(Refreshed)); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchFactorySessionAsync(Profile, _ => null, _ => { saves++; return true; }, cancel.Token));
        Assert.Equal(0, saves);
    }
    [Fact]
    public async Task ReplacedAccountRejectsWritebackAndStopsQuota()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((r, _) =>
        { calls++; Assert.Equal("api.workos.com", r.RequestUri!.Host); return Task.FromResult(Ok(Refreshed)); }));
        var reading = await provider.FetchFactorySessionAsync(Profile, _ => null, _ => false, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Unavailable, reading.State); Assert.Equal(1, calls); Assert.Empty(reading.Windows);
    }
    [Theory]
    [InlineData("""{"refresh_token":"one","refreshToken":"two"}""")]
    [InlineData("""{"refresh_token":"good","client_id":"client_other"}""")]
    [InlineData("""{"refresh_token":"good","organization_id":"../other"}""")]
    [InlineData("""{"refresh_token":5}""")]
    [InlineData("""{"refresh_token":""}""")]
    [InlineData("""{"refresh_token":"has space"}""")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task MalformedProfileMakesNoRequests(string input)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(Ok(Refreshed)); }));
        Assert.Equal(ReadingState.NeedsAuth, (await provider.FetchFactorySessionAsync(input, _ => null, token: TestContext.Current.CancellationToken)).State); Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData("""{"access_token":"valid","organization_id":"org_other"}""")]
    [InlineData("""{"access_token":"has space","refresh_token":"rotated"}""")]
    [InlineData("""{"refresh_token":"rotated"}""")]
    [InlineData("""{"access_token":"valid","refresh_token":{"value":"rotated"}}""")]
    [InlineData("""{"access_token":"valid","refresh_token":5}""")]
    [InlineData("""{"access_token":"valid","refresh_token":""}""")]
    [InlineData("""{"access_token":"valid","organization_id":{"id":"org_other"}}""")]
    [InlineData("""{"access_token":"valid","organization_id":"org_other","organization_id":"org_test"}""")]
    [InlineData("""{"access_token":"first","access_token":"valid"}""")]
    [InlineData("""{"access_token":"valid","refresh_token":"first","refresh_token":"last"}""")]
    [InlineData("not-json")]
    public async Task InvalidOrOtherOrganizationRefreshCannotPersistOrReadQuota(string response)
    {
        var saves = 0; var calls = 0;
        using var provider = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(Ok(response)); }));
        var reading = await provider.FetchFactorySessionAsync(Profile, _ => null, _ => { saves++; return true; }, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Equal(0, saves); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task RecentlyRefreshedRejectedAccessDoesNotRotateContinuously()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((r, _) =>
        {
            calls++; Assert.NotEqual("api.workos.com", r.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }));
        var profile = new FactoryWorkOsProfile("fresh-access", "refresh", null, null, DateTimeOffset.UtcNow).Serialize();
        for (var i = 0; i < 2; i++)
            Assert.Equal(ReadingState.NeedsAuth, (await provider.FetchFactorySessionAsync(profile, _ => null, token: TestContext.Current.CancellationToken)).State);
        Assert.Equal(6, calls);
    }
}
