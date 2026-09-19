using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;
public sealed class OpenCodeZenTests
{
    [Theory]
    [InlineData("""{"data":{"rollingUsage":{"usagePercent":20,"resetInSec":3600},"weeklyUsage":{"usagePercent":40,"resetInSec":86400}}}""")]
    [InlineData("""$R[0]={rollingUsage:$R[1]={usagePercent:20,resetInSec:3600},weeklyUsage:$R[2]={usagePercent:40,resetInSec:86400}};""")]
    public async Task SubscriptionAcceptsJsonAndSolidStart(string quota)
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(async request =>
        {
            calls++; Assert.Equal("auth=fixture", request.Headers.GetValues("Cookie").Single()); Assert.Null(request.Headers.Authorization);
            Assert.Equal("https://opencode.ai", request.Headers.GetValues("Origin").Single());
            if (calls == 1) return Ok("""{"workspaces":[{"id":"wrk_team"}]}""");
            Assert.Contains("wrk_team", Uri.UnescapeDataString(request.RequestUri!.Query)); Assert.Equal(HttpMethod.Get, request.Method);
            await Task.CompletedTask; return Ok(quota);
        }));
        var reading = await provider.FetchAsync("opencode-zen", "Cookie: ignored=tracking; auth=fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(2, calls); Assert.Equal(20, reading.Windows[0].UsedPercent); Assert.Equal(40, reading.Windows[1].UsedPercent);
    }
    [Fact]
    public async Task ExplicitNullUsesBillingWithoutSubscriptionPost()
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(request =>
        {
            calls++; Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Ok(calls == 1 ? "null" : """$R[0]={customerID:"cus_fixture",monthlyUsage:1250000000,monthlyLimit:100,balance:500000000,subscription:null};"""));
        }));
        var reading = await provider.FetchAsync("opencode-zen", "__Host-auth=fixture", key => key == "OPENCODE_WORKSPACE_ID" ? "https://opencode.ai/workspace/wrk_team/billing" : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(2, calls); Assert.Equal(12.5, reading.Headline!.UsedPercent);
        Assert.Equal("USD", reading.Headline.Unit); Assert.Contains("5.00", reading.Windows[1].DisplayValue);
    }
    [Theory]
    [InlineData("""{"monthlyUsage":1000000000}""")]
    [InlineData("""{"customerID":"cus","monthlyUsage":1000000000,"subscription":{}}""")]
    public async Task BillingFallbackRequiresCustomerAndNoLegacySubscription(string billing)
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(_ => Task.FromResult(Ok(++calls == 1 ? "null" : billing))));
        var reading = await provider.FetchAsync("opencode-zen", "auth=fixture", _ => "wrk_team", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Empty(reading.Windows);
    }
    [Fact]
    public async Task WorkspacePostRetryAndSubscriptionPostCarryJsonArgs()
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(async request =>
        {
            calls++;
            if (calls == 1 || calls == 3) return Ok("{}");
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/_server", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (calls == 2) { Assert.Equal(0, body.RootElement.GetArrayLength()); return Ok("""[{"id":"wrk_team"}]"""); }
            Assert.Equal("wrk_team", body.RootElement[0].GetString());
            return Ok("""{"rollingUsage":{"usagePercent":0},"weeklyUsage":{"usagePercent":10}}""");
        }));
        var reading = await provider.FetchAsync("opencode-zen", "auth=fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(4, calls);
    }
    [Fact]
    public async Task SignedOutResponseClearsQuotaWithoutBillingFallback()
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(_ => { calls++; return Task.FromResult(Ok("<html>Sign in</html>")); }));
        var reading = await provider.FetchAsync("opencode-zen", "auth=fixture", _ => "wrk_team", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, reading.State); Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData("""{"primaryWindow":{"used":1,"limit":400,"resetInSec":3600},"secondaryWindow":{"used":30,"limit":100,"resetInSec":86400}}""", 0.25)]
    [InlineData("""{"rollingUsage":{"usagePercent":0.25},"weeklyUsage":{"usagePercent":30}}""", 25)]
    public async Task FractionAndComputedPercentageHaveDifferentUnits(string payload, double expected)
    {
        using var provider = new NativeProviders(new Handler(request =>
        {
            Assert.Contains("wrk_SELECTED", Uri.UnescapeDataString(request.RequestUri!.Query));
            return Task.FromResult(Ok(payload));
        }));
        var reading = await provider.FetchAsync("opencode-zen", "auth=fixture", key => key == "CODEXBAR_OPENCODE_WORKSPACE_ID" ? "wrk_SELECTED" : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(expected, reading.Headline!.UsedPercent);
    }
    [Fact]
    public async Task LargeWorkspaceListAndSeededNullReachPayAsYouGo()
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(request =>
        {
            calls++; Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Ok(calls switch
            {
                1 => new string(' ', 4096) + """[{"id":"wrk_team"}]""",
                2 => """($R["server-fn:fixture"]=[],null)""",
                _ => """{"customerID":"cus","monthlyUsage":200000000,"balance":300000000}"""
            }));
        }));
        var reading = await provider.FetchAsync("opencode-zen", "auth=fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Null(reading.Headline!.UsedPercent); Assert.Equal(3, calls);
    }
    [Fact]
    public async Task ServerFailureLoginHtmlStillClearsExpiredAuthentication()
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(_ =>
        {
            calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("<html>Sign in</html>") });
        }));
        var reading = await provider.FetchAsync("opencode-zen", "auth=fixture", _ => "wrk_team", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, reading.State); Assert.Equal(1, calls);
    }
    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request); }
}
