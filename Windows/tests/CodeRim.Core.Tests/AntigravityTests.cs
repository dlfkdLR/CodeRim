using System.Net;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;
public sealed class AntigravityTests
{
    [Fact]
    public async Task FullModelQuotaMustBeVerifiedBeforeDisplayingIt()
    {
        using var provider = new NativeProviders(new Handler(async request =>
        {
            Assert.Equal("Bearer access", request.Headers.Authorization!.ToString());
            var body = await request.Content!.ReadAsStringAsync();
            if (request.RequestUri!.AbsolutePath.EndsWith(":loadCodeAssist", StringComparison.Ordinal))
            {
                Assert.Contains("ANTIGRAVITY", body);
                return Ok("""{"cloudaicompanionProject":{"id":"project"},"paidTier":{"name":"Ultra"}}""");
            }
            Assert.Contains("project", body);
            if (request.RequestUri.AbsolutePath.EndsWith(":fetchAvailableModels", StringComparison.Ordinal))
                return Ok("""{"models":{"claude":{"displayName":"Claude","quotaInfo":{"remainingFraction":1}}}}""");
            return Ok("""{"buckets":[{"modelId":"claude","remainingFraction":0.5},{"modelId":"claude","remainingFraction":0.2}]}""");
        }));
        var reading = await provider.FetchAsync("gemini", "access", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal("gemini", reading.Id); Assert.Equal(80, reading.Headline!.UsedPercent); Assert.Equal("Claude", reading.Headline.Name);
        Assert.Equal(0, reading.Headline.DurationMinutes); Assert.Equal("Ultra", reading.Plan);
    }
    [Fact]
    public async Task UnverifiableFullAllowanceDoesNotClaimZeroUsage()
    {
        using var provider = new NativeProviders(new Handler(request => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith(":loadCodeAssist", StringComparison.Ordinal) ? Ok("{}") :
            request.RequestUri.AbsolutePath.EndsWith(":fetchAvailableModels", StringComparison.Ordinal) ? Ok("""{"models":{"model":{"quotaInfo":{"remainingFraction":1}}}}""") :
            new HttpResponseMessage(HttpStatusCode.Forbidden))));
        var reading = await provider.FetchAsync("gemini", "access", _ => null, TestContext.Current.CancellationToken);
        Assert.Empty(reading.Windows); Assert.Contains("verified", reading.Message);
    }
    private static HttpResponseMessage Ok(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request); }
}
