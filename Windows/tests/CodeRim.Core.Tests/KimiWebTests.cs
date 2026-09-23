using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
namespace CodeRim.Core.Tests;

// These assertions measure real cancellation deadlines. Concurrent CPU/process
// stress cases otherwise measure runner scheduling instead of the transport bound.
[CollectionDefinition("Transport deadlines", DisableParallelization = true)]
public sealed class TransportDeadlinesDefinition;

[Collection("Transport deadlines")]
public sealed class KimiWebTests
{
    private const string Usage = """{"usages":[{"scope":"FEATURE_CODING","detail":{"limit":"100","used":"25","resetTime":"2026-10-01T00:00:00Z"},"limits":[{"window":{"duration":5,"timeUnit":"TIME_UNIT_HOUR"},"detail":{"limit":"20","remaining":"15"}}]}]}""";
    private const string Stats = """{"subscriptionBalance":{"feature":"FEATURE_OMNI","type":"SUBSCRIPTION","amountUsedRatio":0.42,"kimiCodeUsedRatio":0.99},"ratelimitCode7d":{"ratio":0.17,"resetTime":"2026-10-01T00:00:00Z"}}""";
    private const string Plan = """{"subscription":{"active":true,"status":"SUBSCRIPTION_STATUS_ACTIVE","goods":{"title":"Allegro"}}}""";
    [Fact]
    public async Task WebTransportUsesOnlyItsFixedPostEndpointsAndSelectedToken()
    {
        var calls = new List<string>();
        using var provider = new NativeProviders(new Handler(async (request, cancellationToken) => {
            Assert.Equal("www.kimi.com", request.RequestUri!.Host); Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer web-token", request.Headers.Authorization!.ToString());
            Assert.Equal("kimi-auth=web-token", request.Headers.GetValues("Cookie").Single());
            Assert.Equal("web", request.Headers.GetValues("x-msh-platform").Single());
            var path = request.RequestUri.AbsolutePath; calls.Add(path);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Equal(path.EndsWith("/GetUsages", StringComparison.Ordinal) ? """{"scope":["FEATURE_CODING"]}""" : "{}", body);
            return Ok(path.EndsWith("/GetUsages", StringComparison.Ordinal) ? Usage : path.EndsWith("/GetSubscriptionStats", StringComparison.Ordinal) ? Stats : Plan);
        }));
        var reading = await provider.FetchKimiAsync(new("web", "web-token"), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(3, calls.Count); Assert.Equal("Allegro", reading.Plan);
        Assert.Equal(4, reading.Windows.Count);
        Assert.Equal(42, reading.Windows.Single(w => w.Id == "kimi-monthly").UsedPercent);
        Assert.Equal(17, reading.Windows.Single(w => w.Id == "kimi-code-7d").UsedPercent);
        Assert.All(reading.Windows, window => Assert.Null(window.Unit));
    }
    [Theory]
    [InlineData("api", HttpStatusCode.Forbidden, ReadingState.Error)]
    [InlineData("web", HttpStatusCode.Forbidden, ReadingState.NeedsAuth)]
    [InlineData("api", HttpStatusCode.Unauthorized, ReadingState.NeedsAuth)]
    [InlineData("web", HttpStatusCode.TooManyRequests, ReadingState.Unavailable)]
    [InlineData("web", HttpStatusCode.ServiceUnavailable, ReadingState.Error)]
    public async Task SourceSpecificErrorsStayDistinct(string source, HttpStatusCode code, ReadingState expected)
    {
        using var provider = new NativeProviders(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(code))));
        Assert.Equal(expected, (await provider.FetchKimiAsync(new(source, "token"), TestContext.Current.CancellationToken)).State);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"usages":[]}""")]
    [InlineData("""{"usages":[{"scope":"FEATURE_CODING"}]}""")]
    [InlineData("""{"usages":[{"scope":"FEATURE_CODING","detail":{},"limits":"bad"}]}""")]
    [InlineData("""{"usages":[{"scope":"OTHER","detail":{"limit":100,"used":1}}]}""")]
    [InlineData("""{"usages":[{"scope":"FEATURE_CODING","detail":{"limit":100}}]}""")]
    public async Task MissingMalformedOrUnknownQuotaCannotBecomeFreshZero(string body)
    {
        using var provider = new NativeProviders(new Handler((_, _) => Task.FromResult(Ok(body))));
        var reading = await provider.FetchKimiAsync(new("web", "token"), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Empty(reading.Windows);
    }
    [Fact]
    public async Task StalledPlanKeepsCompletedStatsAndRequiredQuota()
    {
        using var provider = new NativeProviders(new Handler(async (request, token) => {
            if (request.RequestUri!.AbsolutePath.EndsWith("/GetSubscription", StringComparison.Ordinal)) await Task.Delay(10000, token);
            return Ok(request.RequestUri.AbsolutePath.EndsWith("/GetUsages", StringComparison.Ordinal) ? Usage : Stats);
        }));
        var reading = await provider.FetchKimiAsync(new("web", "token"), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Partial, reading.State);
        Assert.Contains(reading.Windows, w => w.Id == "kimi-monthly" && w.UsedPercent == 42);
    }
    [Fact]
    public async Task Optional429CannotThrottleTheNextRequiredRefresh()
    {
        var required = 0;
        using var provider = new NativeProviders(new Handler((request, _) => {
            if (request.RequestUri!.AbsolutePath.EndsWith("/GetUsages", StringComparison.Ordinal)) { required++; return Task.FromResult(Ok(Usage)); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }));
        for (var i = 0; i < 2; i++) Assert.Equal(ReadingState.Partial, (await provider.FetchKimiAsync(new("web", "token"), TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, required);
    }
    [Fact]
    public async Task BrowserAuthCannotEscapeItsPathOrChangeTokenForOptionalEndpoints()
    {
        var jar = new BrowserCookieJar([new("kimi-auth", "selected", "www.kimi.com", "/apiv2/kimi.gateway.billing.v1.BillingService", true, true, 0),
            new("kimi-auth", "other", "www.kimi.com", "/apiv2/kimi.gateway.membership.v2.MembershipService", true, true, 0)], ["kimi.com"]);
        var calls = 0;
        using var provider = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(Ok(Usage)); }));
        var reading = await provider.FetchKimiAsync(new("web", "selected", BrowserState: jar.Serialize()), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Partial, reading.State); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task CliUsesFixedHostAndIdentityHeadersWithoutCookies()
    {
        using var provider = new NativeProviders(new Handler((request, _) => {
            Assert.Equal("https://api.kimi.com/coding/v1/usages", request.RequestUri!.AbsoluteUri);
            Assert.Equal("kimi_code_cli", request.Headers.GetValues("X-Msh-Platform").Single());
            Assert.Equal("fixture-device", request.Headers.GetValues("X-Msh-Device-Id").Single());
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(Ok("""{"usages":{"limit_7d":{"used_ratio":0.33}}}"""));
        }));
        var reading = await provider.FetchKimiAsync(new("cli", "token", ExpiresAt: DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), DeviceId: "fixture-device"), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State);
    }
    [Theory]
    [InlineData("""{"subscriptionBalance":{"feature":123,"amountUsedRatio":0.42}}""")]
    [InlineData("""{"subscriptionBalance":{"type":{},"amountUsedRatio":0.42}}""")]
    [InlineData("""{"ratelimitCode7d":{"enabled":"false","ratio":0.17}}""")]
    public void MalformedOptionalStatsCannotBecomeReadyQuota(string stats)
    {
        using var main = JsonDocument.Parse(Usage); using var extra = JsonDocument.Parse(stats); using var plan = JsonDocument.Parse(Plan);
        var reading = NativeProviders.ParseKimiWeb(main.RootElement, extra.RootElement, plan.RootElement);
        Assert.Equal(ReadingState.Partial, reading.State);
        Assert.DoesNotContain(reading.Windows, window => window.Id is "kimi-monthly" or "kimi-code-7d");
        Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal("limit_7d", reading.Headline.Id);
    }
    [Fact]
    public async Task OptionalJoinIsBoundedEvenIfTransportIgnoresCancellation()
    {
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new NativeProviders(new Handler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/GetSubscription", StringComparison.Ordinal) ? held.Task
            : Task.FromResult(Ok(request.RequestUri.AbsolutePath.EndsWith("/GetUsages", StringComparison.Ordinal) ? Usage : Stats))));
        try
        {
            var reading = await provider.FetchKimiAsync(new("web", "token"), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
            Assert.Equal(ReadingState.Partial, reading.State); Assert.Contains(reading.Windows, w => w.Id == "kimi-monthly");
        }
        finally { held.TrySetResult(Ok(Plan)); }
    }
    [Fact]
    public async Task CallerCancellationDoesNotWaitForAnUncooperativeTransport()
    {
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var provider = new NativeProviders(new Handler((_, _) => held.Task));
        try
        {
            var task = provider.FetchKimiAsync(new("web", "token"), cancel.Token); await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        }
        finally { held.TrySetResult(Ok(Usage)); }
    }
    [Fact]
    public async Task BadRequestIsAConfigurationFailureNotAnAutoFallback()
    {
        using var provider = new NativeProviders(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))));
        var result = await provider.FetchKimiResultAsync(new("api", "token"), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, result.Reading.State); Assert.False(result.CanFallback);
    }
    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("https://user:password@api.kimi.com")]
    public async Task InvalidApiDestinationsNeitherSendNorPermitFallback(string endpoint)
    {
        using var provider = new NativeProviders(new Handler((_, _) => throw new InvalidOperationException("Must not send")));
        var result = await provider.FetchKimiResultAsync(new("api", "token", endpoint), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, result.Reading.State); Assert.False(result.CanFallback);
    }
    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request, token);
    }
}
