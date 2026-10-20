using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;
public sealed class FactoryProviderTests
{
    private static ProviderReading Parse(string limits, string usage = "{}")
    {
        JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
        return NativeProviders.Parse("factory", new Dictionary<string, JsonElement> { ["limits"] = Json(limits), ["usage"] = Json(usage) });
    }
    [Fact]
    public void ModernFactoryPoolsPreserveZeroAndCents()
    {
        var result = Parse("""{"usesTokenRateLimitsBilling":true,"limits":{"standard":{"fiveHour":{"usedPercent":0,"secondsRemaining":3600},"weekly":{"usedPercent":25},"monthly":{"usedPercent":50}},"core":{"fiveHour":{"usedPercent":0},"weekly":{"usedPercent":0},"monthly":{"usedPercent":0}}},"extraUsageBalanceCents":1250}""");
        Assert.Equal(4, result.Windows.Count); Assert.Equal(0, result.Headline!.UsedPercent); Assert.NotNull(result.Headline.ResetsAt);
        Assert.Equal("USD", result.Windows[^1].Unit); Assert.StartsWith("12", result.Windows[^1].DisplayValue);
    }
    [Fact]
    public void LegacyFactoryUsesPersonalTokensAndIgnoresStaleZeroRatio()
    {
        var result = Parse("{}", """{"usage":{"standard":{"userTokens":25,"orgTotalTokensUsed":900,"totalAllowance":100,"usedRatio":0},"premium":{"userTokens":12,"totalAllowance":9999999999999}}}""");
        Assert.Equal(25, result.Headline!.UsedPercent); Assert.Equal(25, result.Headline.UsedCount);
        Assert.Null(result.Windows[1].UsedPercent); Assert.Equal(12, result.Windows[1].UsedCount);
    }
    [Fact]
    public async Task FactoryModernBillingDoesNotMixLegacyTokenUsage()
    {
        using var handler = new Handler(); using var provider = new NativeProviders(handler);
        var result = await provider.FetchAsync("factory", "fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(25, result.Headline!.UsedPercent); Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("{\"usesTokenRateLimitsBilling\":true,\"limits\":null,\"extraUsageBalanceCents\":0}")]
    [InlineData("{\"usesTokenRateLimitsBilling\":true}")]
    public void IncompleteModernBillingKeepsLegacyReading(string limits)
    {
        var reading = Parse(limits, """{"usage":{"standard":{"userTokens":25,"totalAllowance":100}}}""");
        Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal(25, reading.Headline.UsedCount);
    }
    [Theory]
    [InlineData("/api/app/auth/me")]
    [InlineData("/api/organization/subscription/usage")]
    public async Task FactoryRetriesCompleteTransactionOnAlternateHost(string failingPath)
    {
        var hosts = new List<string>();
        using var provider = new NativeProviders(new DelegateHandler(request =>
        {
            var uri = request.RequestUri!; hosts.Add(uri.Host);
            if (uri.Host == "api.factory.ai" && uri.AbsolutePath == failingPath) return new(HttpStatusCode.InternalServerError);
            if (uri.AbsolutePath == "/api/app/auth/me") return Ok("""{"userProfile":{"id":"fixture"}}""");
            if (uri.AbsolutePath == "/api/billing/limits") return Ok("{}");
            return Ok("""{"userId":"fixture","usage":{"standard":{"userTokens":25,"totalAllowance":100}}}""");
        }));
        var result = await provider.FetchAsync("factory", "fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(25, result.Headline!.UsedPercent); Assert.Contains("app.factory.ai", hosts);
    }
    [Fact]
    public async Task FactoryUsesBearerSubjectWhenProfileHasNoIdAndRejectsDifferentUser()
    {
        var mismatch = false;
        using var provider = new NativeProviders(new DelegateHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/api/app/auth/me") return Ok("{}");
            if (uri.AbsolutePath == "/api/billing/limits") return Ok("{}");
            Assert.Contains("userId=member", uri.Query);
            return Ok(mismatch ? """{"userId":"different","usage":{"standard":{"userTokens":25,"totalAllowance":100}}}""" : """{"userId":"member","usage":{"standard":{"userTokens":25,"totalAllowance":100}}}""");
        }));
        const string bearer = "e30.eyJzdWIiOiJtZW1iZXIifQ.signature";
        var result = await provider.FetchAsync("factory", bearer, _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(25, result.Headline!.UsedPercent);
        mismatch = true;
        result = await provider.FetchAsync("factory", bearer, _ => null, TestContext.Current.CancellationToken);
        Assert.Empty(result.Windows);
    }
    [Fact]
    public async Task FactoryPreservesAuthFailureWhenFallbackHostIsMissing()
    {
        using var provider = new NativeProviders(new DelegateHandler(request => new(request.RequestUri!.Host == "api.factory.ai" ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound)));
        Assert.Equal(ReadingState.NeedsAuth, (await provider.FetchAsync("factory", "fixture", _ => null, TestContext.Current.CancellationToken)).State);
    }
    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request)); }
    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Assert.Equal("web-app", request.Headers.GetValues("x-factory-client").Single()); Assert.Equal("Bearer fixture", request.Headers.Authorization!.ToString());
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/api/app/auth/me" => """{"userProfile":{"id":"fixture-user"}}""",
                "/api/billing/limits" => """{"usesTokenRateLimitsBilling":true,"limits":{"standard":{"fiveHour":{"usedPercent":25}}}}""",
                _ => throw new InvalidOperationException("Legacy endpoint should not be called")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
