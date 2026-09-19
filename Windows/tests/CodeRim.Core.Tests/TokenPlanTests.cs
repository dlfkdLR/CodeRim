using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;
public sealed class TokenPlanTests
{
    [Theory]
    [InlineData("qwencloud", "", "cs-data.qwencloud.com", "QWENCLOUD")]
    [InlineData("alibabatokenplan", "cn-personal", "bailian-cs.console.aliyun.com", "BAILIAN_ALIYUN")]
    [InlineData("alibabatokenplan", "intl-personal", "bailian-singapore-cs.alibabacloud.com", "MODELSTUDIO_ALBABACLOUD")]
    public async Task PersonalPlanFetchesRollingRatiosAndOptionalPlanLimits(string id, string region, string gateway, string site)
    {
        using var provider = new NativeProviders(new Handler(async request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("session=fixture; csrf=csrf-fixture", request.Headers.GetValues("Cookie").Single());
            if (request.RequestUri!.AbsolutePath != "/data/api.json")
            {
                Assert.Equal("navigate", request.Headers.GetValues("Sec-Fetch-Mode").Single());
                Assert.Contains("text/html", request.Headers.Accept.ToString());
                return Ok("""<script>window.CONFIG={SEC_TOKEN: "fixture-sec"}</script>""");
            }
            Assert.Equal(gateway, request.RequestUri.Host);
            var form = (await request.Content!.ReadAsStringAsync()).Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1].Replace('+', ' ')));
            Assert.Equal("fixture-sec", form["sec_token"]); Assert.Contains(site, form["params"]); Assert.DoesNotContain("switchAgent", form["params"]);
            Assert.Equal("csrf-fixture", request.Headers.GetValues("x-csrf-token").Single());
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (query.Contains("/quota-config", StringComparison.Ordinal)) return Ok("""{"data":{"pro":{"five_hour":100,"weekly":1000}}}""");
            if (query.Contains("/subscription", StringComparison.Ordinal)) return Ok("""{"data":{"specCode":"pro"}}""");
            return Ok("""{"data":"{\"per5HourPercentage\":0.25,\"per1WeekPercentage\":0.6,\"per5HourResetTime\":\"2026-09-20T00:00:00Z\"}"}""");
        }));
        var reading = await provider.FetchAsync(id, "session=fixture; csrf=csrf-fixture", key => key == "ALIBABA_TOKEN_PLAN_REGION" ? region : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(25, reading.Windows[0].UsedPercent); Assert.Equal(60, reading.Windows[1].UsedPercent);
        Assert.Contains("100", reading.Windows[0].DisplayValue); Assert.Equal("pro", reading.Plan);
    }
    [Fact]
    public async Task TeamPlanUsesSummaryAndDoesNotReportCreditsAsTokens()
    {
        using var provider = new NativeProviders(new Handler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("BssOpenAPI-V3", body); Assert.Contains("sfm_tokenplanteams_dp_intl", Uri.UnescapeDataString(body));
            return Ok("""{"data":{"TotalCount":1,"EquityList":[{"TotalValue":1000,"TotalSurplusValue":750,"CycleEndTime":"2026-10-01T00:00:00Z"}]}}""");
        }));
        var reading = await provider.FetchAsync("alibabatokenplan", "session=fixture", key => key == "ALIBABA_TOKEN_PLAN_SEC_TOKEN" ? "sec" : null, TestContext.Current.CancellationToken);
        Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal("credits", reading.Headline.Unit);
    }
    [Theory]
    [InlineData("NeedLogin", ReadingState.NeedsAuth)]
    [InlineData("BailianGateway.Workspace.NotAuthorised", ReadingState.Error)]
    public async Task NestedErrorCannotMasqueradeAsAQuota(string code, ReadingState expected)
    {
        using var provider = new NativeProviders(new Handler(_ => Task.FromResult(Ok(JsonSerializer.Serialize(new { code = 200, successResponse = true,
            data = new { success = false, errorCode = code, per5HourPercentage = 0.2 } })))));
        var reading = await provider.FetchAsync("qwencloud", "session=fixture", _ => "sec", TestContext.Current.CancellationToken);
        Assert.Equal(expected, reading.State); Assert.Empty(reading.Windows);
    }
    [Theory]
    [InlineData("""{"usageUnitsRemaining":80,"usageUnitsAvailable":100}""")]
    [InlineData("""{"usageUnitsConsumedThisBillingCycle":20,"usageUnitsAvailable":100}""")]
    [InlineData("""{"usageUnitsConsumedThisBillingCycle":20,"usageUnitsRemaining":80}""")]
    public void AugmentSupportsPartiallyReportedCreditFields(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var reading = NativeProviders.Parse("augment", new Dictionary<string, JsonElement> { ["main"] = document.RootElement });
        Assert.Equal(20, reading.Headline!.UsedPercent);
    }

    [Theory]
    [InlineData("cookie")]
    [InlineData("userinfo")]
    [InlineData("transient")]
    public async Task QwenDiscoveryPreservesFallbacksAndTransientFailures(string mode)
    {
        using var provider = new NativeProviders(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/data/api.json") return Task.FromResult(Ok("""{"per5HourPercentage":0.2}"""));
            if (request.RequestUri.AbsolutePath == "/tool/user/info.json" && mode == "userinfo") return Task.FromResult(Ok("""{"csrfToken":"from-user"}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));
        var reading = await provider.FetchAsync("qwencloud", "session=fixture" + (mode == "cookie" ? "; sec_token=cookie-sec" : ""), _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(mode == "transient" ? ReadingState.Error : ReadingState.Ready, reading.State);
    }
    [Fact]
    public void ExplicitEmptySubscriptionClearsOldQuota()
    {
        using var doc = JsonDocument.Parse("""{"data":{"TotalValue":"0","TotalSurplusValue":"0","TotalCount":0}}""");
        var reading = NativeProviders.Parse("qwencloud", new Dictionary<string, JsonElement> { ["main"] = doc.RootElement });
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Empty(CodeRim.Core.Services.ReadingRetention.Merge(reading, new("qwencloud", ReadingState.Ready, [new("quota", "Old", 50)])).Windows);
    }

    private static HttpResponseMessage Ok(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request); }
}
