using System.Net;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;
public sealed class ZoomMateProviderTests
{
    [Fact]
    public async Task CookieBootstrapAndHostFallbackPreserveAuthBoundary()
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(request =>
        {
            calls++; Assert.Equal("session=fixture", request.Headers.GetValues("Cookie").Single());
            if (request.RequestUri!.AbsolutePath.EndsWith("/login/", StringComparison.Ordinal))
            {
                Assert.Null(request.Headers.Authorization); return Ok("""{"data":{"nak":"fixture-token"}}""");
            }
            Assert.Equal("Bearer fixture-token", request.Headers.Authorization!.ToString());
            return request.RequestUri.Host == "ai.zoom.us" ? new(HttpStatusCode.ServiceUnavailable)
                : Ok("""{"data":{"credit_status":{"used_credit":25,"budget_cap":100}}}""");
        }));
        var reading = await provider.FetchAsync("zoommate", "Cookie: session=fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(3, calls); Assert.Equal(25, reading.Headline!.UsedPercent);
    }
    [Fact]
    public async Task UnlimitedCreditsHaveNoInventedPercentageAndAuthDoesNotFailover()
    {
        var fail = false; var calls = 0;
        using var provider = new NativeProviders(new Handler(request =>
        {
            calls++; Assert.Equal("Bearer fixture", request.Headers.Authorization!.ToString());
            return fail ? new(HttpStatusCode.Unauthorized) : Ok("""{"data":{"credit_status":{"used_credit":25,"budget_cap":100,"is_unlimited":true}}}""");
        }));
        var reading = await provider.FetchAsync("zoommate", "Bearer fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Null(reading.Headline!.UsedPercent); Assert.Equal("Unlimited", reading.Headline.DisplayValue);
        fail = true;
        Assert.Equal(ReadingState.NeedsAuth, (await provider.FetchAsync("zoommate", "fixture", _ => null, TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, calls);
    }
    private static HttpResponseMessage Ok(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request)); }
}
