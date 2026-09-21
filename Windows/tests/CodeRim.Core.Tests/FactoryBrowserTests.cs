using System.Net;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;

public sealed class FactoryBrowserTests
{
    private const string Limits = """{"usesTokenRateLimitsBilling":true,"limits":{"standard":{"fiveHour":{"usedPercent":25}}}}""";
    private const string Usage = """{"userId":"member","usage":{"standard":{"userTokens":25,"totalAllowance":100}}}""";
    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static HttpResponseMessage Data(HttpRequestMessage r) => Ok(r.RequestUri!.AbsolutePath switch
    { "/api/app/auth/me" => """{"userProfile":{"id":"member"}}""", "/api/billing/limits" => Limits, _ => Usage });
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token); }
    [Theory]
    [InlineData("fixture")]
    [InlineData("Bearer fixture")]
    [InlineData("Authorization: Bearer fixture")]
    [InlineData("Authorization: \"Bearer fixture\"")]
    [InlineData("Authorization: 'Bearer fixture'")]
    public async Task BareAndPastedBearerCredentialsRemainBearerOnly(string input)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((r, _) =>
        {
            calls++; Assert.False(r.Headers.Contains("Cookie")); Assert.Equal("Bearer fixture", r.Headers.Authorization!.ToString());
            return Task.FromResult(Data(r));
        }));
        var reading = await provider.FetchAsync("factory", input, _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal(2, calls);
    }
    [Theory]
    [InlineData("session=fixture")]
    [InlineData("Cookie: session=fixture")]
    [InlineData("Cookie: 'session=fixture'")]
    public async Task ManualCookieAuthenticatesWithoutInventingBearer(string input)
    {
        using var provider = new NativeProviders(new Handler((r, _) =>
        {
            Assert.Null(r.Headers.Authorization); Assert.Equal("session=fixture", Assert.Single(r.Headers.GetValues("Cookie")));
            return Task.FromResult(Data(r));
        }));
        var reading = await provider.FetchAsync("factory", input, _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(25, reading.Headline!.UsedPercent);
    }
    [Theory]
    [InlineData("app.factory.ai")]
    [InlineData("auth.factory.ai")]
    public async Task HostOnlyImportedSessionNeverLeaksToOtherFactoryHosts(string host)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((r, _) =>
        {
            calls++; Assert.Equal(host, r.RequestUri!.Host); Assert.Null(r.Headers.Authorization);
            Assert.Equal("session=host-only", Assert.Single(r.Headers.GetValues("Cookie")));
            return Task.FromResult(Data(r));
        }));
        var reading = await provider.FetchAsync("factory", null, _ => null, uri => uri.Host == host ? "session=host-only" : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal(2, calls);
    }
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    public async Task StaleAccessCookieCanRetryWithTheSameScopedCookieOnly(int status)
    {
        var bearerCalls = 0; var cookieCalls = 0;
        using var provider = new NativeProviders(new Handler((r, _) =>
        {
            Assert.Equal("access-token=stale; session=valid", Assert.Single(r.Headers.GetValues("Cookie")));
            if (r.Headers.Authorization is not null) { bearerCalls++; Assert.Equal("stale", r.Headers.Authorization.Parameter); return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)); }
            cookieCalls++; return Task.FromResult(Data(r));
        }));
        var reading = await provider.FetchAsync("factory", "Cookie: access-token=stale; session=valid", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(1, bearerCalls); Assert.Equal(2, cookieCalls);
    }
    [Fact]
    public async Task ServiceFailuresDoNotStripAuthorizationAndRetryTheSameRequest()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((r, _) =>
        {
            calls++; Assert.NotNull(r.Headers.Authorization); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));
        var reading = await provider.FetchAsync("factory", "Cookie: access-token=value; session=valid", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Equal(3, calls);
    }
    [Fact]
    public async Task RateLimitDoesNotRetryEveryFactoryOrigin()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) =>
        { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)); }));
        var reading = await provider.FetchAsync("factory", "Cookie: session=valid", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Unavailable, reading.State); Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData("Cookie:")]
    [InlineData("Authorization: Basic fixture")]
    [InlineData("Bearer")]
    [InlineData("Cookie: name=value; malformed")]
    [InlineData("Cookie: access-token=first; access-token=second")]
    public async Task MalformedExplicitInputNeverFallsBackToAnotherCredential(string input)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(Data(new HttpRequestMessage(HttpMethod.Get, "https://api.factory.ai/api/app/auth/me"))); }));
        var reading = await provider.FetchAsync("factory", input, _ => "different-account", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, reading.State); Assert.Equal(0, calls);
    }
    [Fact]
    public async Task CookieLoginRedirectIsAuthenticationFailure()
    {
        using var provider = new NativeProviders(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri("https://unrelated.invalid/login"); return Task.FromResult(response);
        }));
        var reading = await provider.FetchAsync("factory", "Cookie: session=expired", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, reading.State); Assert.Empty(reading.Windows);
    }
    [Fact]
    public async Task CancellationStopsCookieRetryAndHostFallback()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) =>
        {
            calls++; if (calls == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            cancel.Cancel(); throw new OperationCanceledException(cancel.Token);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchAsync("factory", "Cookie: access-token=stale; session=valid", _ => null, cancel.Token));
        Assert.Equal(2, calls);
    }
    private const string OldJwt = "e30.eyJzdWIiOiJvbGQtbWVtYmVyIn0.signature";
    [Fact]
    public async Task RejectedBearerSubjectCannotOverrideCookieAuthenticatedUser()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((r, _) =>
        {
            calls++;
            if (r.Headers.Authorization is not null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if (r.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("old-member", r.RequestUri.Query);
                return Task.FromResult(Ok("""{"userId":"current-member","usage":{"standard":{"userTokens":7,"totalAllowance":100}}}"""));
            }
            return Task.FromResult(Ok("{}"));
        }));
        var reading = await provider.FetchAsync("factory", "Cookie: access-token=" + OldJwt + "; session=current-member", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(7, reading.Headline!.UsedCount); Assert.Equal(4, calls);
    }
    [Theory]
    [InlineData("/api/billing/limits")]
    [InlineData("/api/organization/subscription/usage")]
    public async Task AuthenticationFallbackAtLaterStageRestartsProfileAndUsageTogether(string rejectedPath)
    {
        var authUsers = new List<string>(); var usageUsers = new List<string>();
        using var provider = new NativeProviders(new Handler((r, _) =>
        {
            var hasBearer = r.Headers.Authorization is not null;
            var current = hasBearer ? "old-member" : "current-member";
            if (r.RequestUri!.AbsolutePath == "/api/app/auth/me")
            {
                authUsers.Add(current);
                return Task.FromResult(Ok(System.Text.Json.JsonSerializer.Serialize(new { userProfile = new { id = current } })));
            }
            if (hasBearer && r.RequestUri.AbsolutePath == rejectedPath) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if (r.RequestUri.AbsolutePath == "/api/billing/limits") return Task.FromResult(Ok("{}"));
            usageUsers.Add(current);
            Assert.Contains("userId=" + current, r.RequestUri.Query);
            return Task.FromResult(Ok("""{"userId":"current-member","usage":{"standard":{"userTokens":7,"totalAllowance":100}}}"""));
        }));
        var reading = await provider.FetchAsync("factory", "Cookie: access-token=" + OldJwt + "; session=current-member", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(7, reading.Headline!.UsedCount);
        Assert.Collection(authUsers, user => Assert.Equal("old-member", user), user => Assert.Equal("current-member", user)); Assert.Equal("current-member", Assert.Single(usageUsers));
    }
    [Fact]
    public async Task OptionalBillingRateLimitStopsAndBacksOffSubsequentFetch()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((r, _) =>
        {
            calls++;
            return Task.FromResult(r.RequestUri!.AbsolutePath == "/api/app/auth/me" ? Data(r) : new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reading = await provider.FetchAsync("factory", "Cookie: session=valid", _ => null, TestContext.Current.CancellationToken);
            Assert.Equal(ReadingState.Unavailable, reading.State); Assert.Empty(reading.Windows);
        }
        Assert.Equal(2, calls);
    }
    [Theory]
    [InlineData("access-token=stale; __recent_auth=stale; __Secure-authjs.session-token=valid", "access-token=", "__recent_auth=")]
    [InlineData("session=stale; wos-session=stale; __Secure-authjs.session-token=valid", "session=", "wos-session=")]
    [InlineData("access-token=stale; session=stale; __Secure-authjs.session-token=valid", "access-token=", "session=")]
    [InlineData("other=stale; __Secure-authjs.session-token=valid", "other=", "other=")]
    public async Task ConflictingCookiesHaveBoundedSameProfileTransactionRecovery(string cookies, string badA, string badB)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((r, _) =>
        {
            calls++; Assert.Equal("api.factory.ai", r.RequestUri!.Host);
            var header = Assert.Single(r.Headers.GetValues("Cookie"));
            Assert.Contains("__Secure-authjs.session-token=valid", header);
            if (header.StartsWith(badA, StringComparison.Ordinal) || header.Contains("; " + badA, StringComparison.Ordinal)
                || header.StartsWith(badB, StringComparison.Ordinal) || header.Contains("; " + badB, StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
            Assert.Null(r.Headers.Authorization); return Task.FromResult(Data(r));
        }));
        var reading = await provider.FetchAsync("factory", null, _ => null, uri => uri.Host == "api.factory.ai" ? cookies : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.InRange(calls, 3, 7);
    }
    [Fact]
    public async Task ConflictRecoveryIsBoundedEvenIfEveryVariantFails()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((r, _) =>
        {
            calls++; Assert.NotEmpty(Assert.Single(r.Headers.GetValues("Cookie")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        }));
        var reading = await provider.FetchAsync("factory", "Cookie: access-token=stale; session=stale; other=stale; authjs.session-token=stale", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.InRange(calls, 1, 8);
    }
    [Fact]
    public async Task RemovingOnlyAvailableCookieDoesNotSendAnUnauthenticatedRequest()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((r, _) =>
        {
            calls++; Assert.Equal("access-token=stale", Assert.Single(r.Headers.GetValues("Cookie")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        }));
        var reading = await provider.FetchAsync("factory", "Cookie: access-token=stale", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Equal(4, calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" member ")]
    public async Task NormalizesUserIdsBeforeFallbackAndComparison(string profileId)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((r, _) =>
        {
            calls++;
            if (r.RequestUri!.AbsolutePath == "/api/app/auth/me") return Task.FromResult(Ok(System.Text.Json.JsonSerializer.Serialize(new { userProfile = new { id = profileId } })));
            if (r.RequestUri.AbsolutePath == "/api/billing/limits") return Task.FromResult(Ok("{}"));
            Assert.Contains("userId=member", r.RequestUri.Query);
            return Task.FromResult(Ok("""{"userId":" member ","usage":{"standard":{"userTokens":7,"totalAllowance":100}}}"""));
        }));
        var reading = await provider.FetchAsync("factory", "e30.eyJzdWIiOiIgbWVtYmVyICJ9.signature", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(7, reading.Headline!.UsedCount); Assert.Equal(3, calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedProfileOrReturnedUserIsRejected(bool returned)
    {
        var usageCalls = 0; using var provider = new NativeProviders(new Handler((r, _) =>
        {
            if (r.RequestUri!.AbsolutePath == "/api/app/auth/me") return Task.FromResult(Ok(System.Text.Json.JsonSerializer.Serialize(new { userProfile = new { id = returned ? "member" : new string('x', 257) } })));
            if (r.RequestUri.AbsolutePath == "/api/billing/limits") return Task.FromResult(Ok("{}"));
            usageCalls++; Assert.Contains("userId=member", r.RequestUri.Query);
            return Task.FromResult(Ok(System.Text.Json.JsonSerializer.Serialize(new { userId = new string('x', 257), usage = new { standard = new { userTokens = 7, totalAllowance = 100 } } })));
        }));
        var reading = await provider.FetchAsync("factory", "fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Empty(reading.Windows); Assert.Equal(returned ? 3 : 0, usageCalls);
    }

}
