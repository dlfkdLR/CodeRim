using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
namespace CodeRim.Core.Tests;
public sealed class CloudRegressionTests
{
    private static readonly string[] AwsExportArgs = ["configure", "export-credentials", "--profile", "team production", "--format", "process"];
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExportedAwsBundleNeverMixesEnvironmentCredentials(bool temporary)
    {
        using var provider = new NativeProviders(new Handler(request =>
        {
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=bundle/", request.Headers.GetValues("Authorization").Single());
            Assert.Equal(temporary, request.Headers.Contains("X-Amz-Security-Token"));
            if (temporary) Assert.Equal("bundle-session", request.Headers.GetValues("X-Amz-Security-Token").Single());
            return Ok(request.RequestUri!.Host.StartsWith("ce.", StringComparison.Ordinal) ? """{"ResultsByTime":[]}""" : """{"MetricDataResults":[]}""");
        }));
        var auth = JsonSerializer.Serialize(new { AccessKeyId = "bundle", SecretAccessKey = "secret", SessionToken = temporary ? "bundle-session" : null });
        var reading = await provider.FetchAsync("bedrock", auth, key => key switch { "AWS_ACCESS_KEY_ID" => "environment", "AWS_SESSION_TOKEN" => "old-session", _ => null }, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State);
    }
    [Theory]
    [InlineData("DataUnavailableException", true)]
    [InlineData("com.amazonaws.cost#DataUnavailableException", true)]
    [InlineData("DataUnavailableExceptionExtra", false)]
    [InlineData("AccessDeniedException", false)]
    public async Task OnlyPendingCostDataAllowsActivityToContinue(string error, bool success)
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(request =>
        {
            calls++;
            return request.RequestUri!.Host.StartsWith("ce.", StringComparison.Ordinal)
                ? new(HttpStatusCode.BadRequest) { Content = new StringContent(JsonSerializer.Serialize(new { __type = error })) }
                : Ok("""{"MetricDataResults":[]}""");
        }));
        var reading = await provider.FetchAsync("bedrock", "secret", key => key == "AWS_ACCESS_KEY_ID" ? "fixture" : null, TestContext.Current.CancellationToken);
        Assert.Equal(success ? ReadingState.Ready : ReadingState.Error, reading.State); Assert.Equal(success ? 2 : 1, calls);
    }
    [Fact]
    public async Task AwsRefundsRemainInNetCostHistory()
    {
        using var provider = new NativeProviders(new Handler(request => Ok(request.RequestUri!.Host.StartsWith("ce.", StringComparison.Ordinal)
            ? """{"ResultsByTime":[{"TimePeriod":{"Start":"2026-09-01"},"Groups":[{"Keys":["Amazon Bedrock"],"Metrics":{"UnblendedCost":{"Amount":"30","Unit":"USD"}}},{"Keys":["Amazon Bedrock"],"Metrics":{"UnblendedCost":{"Amount":"-5","Unit":"USD"}}}]}]}"""
            : """{"MetricDataResults":[]}""")));
        var reading = await provider.FetchAsync("bedrock", "secret", key => key switch { "AWS_ACCESS_KEY_ID" => "fixture", "CODEXBAR_BEDROCK_BUDGET" => "100", _ => null }, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal(2, reading.CostUsage!.Entries.Count);
        Assert.Equal(-5, reading.CostUsage.Entries[1].Cost); reading.CostUsage.Validate();
        Assert.Throws<InvalidDataException>(() => (reading.CostUsage with { AllowCredits = false }).Validate());
    }
    [Fact]
    public async Task CloudWatchInt64OverflowPreservesBillingOnly()
    {
        using var provider = new NativeProviders(new Handler(request => Ok(request.RequestUri!.Host.StartsWith("ce.", StringComparison.Ordinal)
            ? """{"ResultsByTime":[]}""" : """{"MetricDataResults":[{"Id":"inputTokens","StatusCode":"Complete","Values":[9223372036854775808]}]}""")));
        var reading = await provider.FetchAsync("bedrock", "secret", key => key == "AWS_ACCESS_KEY_ID" ? "fixture" : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Partial, reading.State); Assert.Single(reading.Windows);
    }
    [Fact]
    public async Task AwsProfileUsesArgumentListAndSelectedRegion()
    {
        var calls = new List<string[]>();
        var profile = await BedrockAuthentication.ResolveAsync(key => key == "AWS_PROFILE" ? "team production" : null, args =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(args.Contains("export-credentials") ? """{"Version":1,"AccessKeyId":"test","SecretAccessKey":"secret","SessionToken":"session","Expiration":"2099-01-01T00:00:00Z"}""" : " eu-central-1 \n");
        });
        Assert.Equal(AwsExportArgs, calls[0]);
        Assert.Equal("eu-central-1", profile.Region); Assert.Contains("\"SessionToken\":\"session\"", profile.Credential);
    }
    [Theory]
    [InlineData("""{"AccessKeyId":"key"}""")]
    [InlineData("""{"AccessKeyId":"key","SecretAccessKey":"secret","Expiration":"2000-01-01T00:00:00Z"}""")]
    [InlineData("""{"AccessKeyId":"key","SecretAccessKey":"secret","Expiration":"invalid"}""")]
    public async Task IncompleteOrExpiredProfilesAreRejected(string payload)
        => await Assert.ThrowsAsync<InvalidDataException>(() => BedrockAuthentication.ResolveAsync(_ => null, _ => Task.FromResult(payload)));
    [Fact]
    public async Task UnsetAwsProfileRegionUsesDefault()
    {
        var result = await BedrockAuthentication.ResolveAsync(_ => null, args => args.Contains("export-credentials")
            ? Task.FromResult("""{"AccessKeyId":"key","SecretAccessKey":"secret"}""") : Task.FromException<string>(new IOException()));
        Assert.Equal("us-east-1", result.Region);
        Assert.True(BedrockAuthentication.UseProfile(null, _ => null)); Assert.False(BedrockAuthentication.UseProfile("key", _ => null));
        Assert.True(BedrockAuthentication.UseProfile("key", key => key == "CODEXBAR_BEDROCK_AUTH_MODE" ? "profile" : null));
    }
    [Theory]
    [InlineData("""{"models":{}}""")]
    [InlineData("""{"models":{"claude":{"displayName":"Claude"}}}""")]
    public async Task EmptyAntigravityModelResultClearsOldAllowances(string result)
    {
        using var provider = new NativeProviders(new Handler(request => Ok(request.RequestUri!.AbsolutePath.EndsWith(":loadCodeAssist", StringComparison.Ordinal) ? "{}" : result)));
        var reading = await provider.FetchAsync("gemini", "access", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Empty(reading.Windows); Assert.NotNull(reading.UpdatedAt);
    }
    [Fact]
    public async Task DoubaoAgentOnlyUsesAgentLabelAndDefaultRegion()
    {
        using var provider = new NativeProviders(new Handler(request =>
        {
            Assert.Contains("/cn-beijing/ark/request", request.Headers.GetValues("Authorization").Single());
            return Ok(request.RequestUri!.Query.Contains("GetAFPUsage", StringComparison.Ordinal)
                ? """{"Result":{"AFPFiveHour":{"Quota":100,"Used":25}}}""" : """{"Result":{"Status":"Reclaimed"}}""");
        }));
        var reading = await provider.FetchAsync("doubao", " secret ", key => key switch { "VOLCENGINE_ACCESS_KEY_ID" => " ", "VOLCENGINE_ACCESS_KEY" => " fixture ", "VOLCENGINE_REGION" => "", _ => null }, TestContext.Current.CancellationToken);
        Assert.Equal("Agent Plan", reading.Plan); Assert.Equal(25, reading.Headline!.UsedPercent);
    }
    [Fact]
    public void IncompleteStaticKeyPairDoesNotShadowSelectedProfile()
    {
        Assert.True(BedrockAuthentication.UseProfile("leftover-secret", key => key == "AWS_PROFILE" ? "work" : null));
        Assert.False(BedrockAuthentication.UseProfile("secret", key => key switch { "AWS_PROFILE" => "work", "AWS_ACCESS_KEY_ID" => "key", _ => null }));
        Assert.Contains("AWS_DEFAULT_PROFILE", NativeProviders.ScopeAliases("bedrock"));
    }
    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request)); }
}
