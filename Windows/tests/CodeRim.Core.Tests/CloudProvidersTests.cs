using System.Net;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;
public sealed class CloudProvidersTests
{
    [Fact]
    public async Task DoubaoSignsOnlyQuotaRequestsAndSeparatesAgentPoints()
    {
        using var provider = new NativeProviders(new Handler(request =>
        {
            Assert.Equal("open.volcengineapi.com", request.RequestUri!.Host);
            Assert.StartsWith("HMAC-SHA256 Credential=fixture/", request.Headers.GetValues("Authorization").Single());
            Assert.Contains("SignedHeaders=content-type;host;x-content-sha256;x-date", request.Headers.GetValues("Authorization").Single());
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", request.Headers.GetValues("X-Content-Sha256").Single());
            return Task.FromResult(Ok(request.RequestUri.Query.Contains("GetAFPUsage", StringComparison.Ordinal)
                ? """{"Result":{"AFPFiveHour":{"Quota":100,"Used":25,"ResetTime":1800000000000}}}"""
                : """{"Result":{"Status":"Active","QuotaUsage":[{"Level":"session","Percent":10,"ResetTimestamp":1800000000}]}}"""));
        }));
        var reading = await provider.FetchAsync("doubao", "secret", key => key == "VOLCENGINE_ACCESS_KEY_ID" ? "fixture" : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(10, reading.Windows[0].UsedPercent); Assert.Equal(25, reading.Windows[1].UsedPercent);
        Assert.Equal("AFP", reading.Windows[1].Unit); Assert.Equal(reading.Windows[0].ResetsAt, reading.Windows[1].ResetsAt);
    }
    [Fact]
    public async Task InvalidAgentResponseIsNotAnEmptyPlan()
    {
        using var provider = new NativeProviders(new Handler(request => Task.FromResult(Ok(
            request.RequestUri!.Query.Contains("GetAFPUsage", StringComparison.Ordinal) ? """{"Result":null}""" : """{"Result":{"Status":"Reclaimed"}}"""))));
        var reading = await provider.FetchAsync("doubao", "secret", key => key == "VOLCENGINE_ACCESS_KEY_ID" ? "fixture" : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State);
    }
    [Fact]
    public async Task BedrockKeepsMonthlyCostsSeparateFromFourteenDayActivity()
    {
        using var provider = new NativeProviders(new Handler(async request =>
        {
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=fixture/", request.Headers.GetValues("Authorization").Single());
            Assert.Equal("session", request.Headers.GetValues("X-Amz-Security-Token").Single());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (request.RequestUri!.Host.StartsWith("ce.", StringComparison.Ordinal))
            {
                Assert.Equal("DAILY", body.RootElement.GetProperty("Granularity").GetString());
                return Ok("""{"ResultsByTime":[{"TimePeriod":{"Start":"2026-09-01"},"Groups":[{"Keys":["Amazon Bedrock"],"Metrics":{"UnblendedCost":{"Amount":"25","Unit":"USD"}}},{"Keys":["Other Service"],"Metrics":{"UnblendedCost":{"Amount":"1000","Unit":"USD"}}}]}]}""");
            }
            Assert.Contains("claude", body.RootElement.GetRawText()); Assert.Equal(3, body.RootElement.GetProperty("MetricDataQueries").GetArrayLength());
            return Ok("""{"MetricDataResults":[{"Id":"inputTokens","StatusCode":"Complete","Values":[100,50]},{"Id":"outputTokens","StatusCode":"Complete","Values":[20]},{"Id":"requests","StatusCode":"Complete","Values":[3]}]}""");
        }));
        string? Setting(string key) => key switch { "AWS_ACCESS_KEY_ID" => "fixture", "AWS_SESSION_TOKEN" => "session", "CODEXBAR_BEDROCK_BUDGET" => "100", _ => null };
        var reading = await provider.FetchAsync("bedrock", "secret", Setting, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(25, reading.Headline!.UsedPercent);
        Assert.Equal(150, reading.Windows[1].UsedCount); Assert.Equal("tokens", reading.Windows[1].Unit);
        Assert.Equal(25, Assert.Single(reading.CostUsage!.Entries).Cost);
    }
    [Fact]
    public async Task IncompleteCloudWatchDoesNotTurnIntoZeroActivity()
    {
        using var provider = new NativeProviders(new Handler(request => Task.FromResult(Ok(request.RequestUri!.Host.StartsWith("ce.", StringComparison.Ordinal)
            ? """{"ResultsByTime":[]}""" : """{"MetricDataResults":[{"Id":"inputTokens","StatusCode":"PartialData","Values":[1]}]}"""))));
        var reading = await provider.FetchAsync("bedrock", "secret", key => key == "AWS_ACCESS_KEY_ID" ? "fixture" : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Partial, reading.State); Assert.Single(reading.Windows);
    }
    private static HttpResponseMessage Ok(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request); }
}
