using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;

public sealed class LedgerProviderTests
{
    [Fact]
    public async Task AiAndFollowsBothEscapedCursorsAndKeepsCurrency()
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            calls++; Assert.Equal("Bearer fixture", request.Headers.Authorization!.ToString());
            if (calls == 1) return Ok("""{"data":[{"cost":"1.25","currency":"usd"},{"cost":"200","currency":"eur"}],"has_more":true,"next_after":"2026-09-19T00:00:00+00","next_after_id":"id+1"}""");
            Assert.Contains("%2B00", request.RequestUri!.OriginalString); Assert.Contains("after_id=id%2B1", request.RequestUri.OriginalString);
            return Ok("""{"data":[{"cost":"2.50","currency":"USD"}],"has_more":false}""");
        });
        using var provider = new NativeProviders(handler);
        var reading = await provider.FetchAsync("aiand", "fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(2, calls); Assert.Null(reading.Headline!.UsedPercent); Assert.Equal("USD", reading.Headline.Unit); Assert.StartsWith("3", reading.Headline.DisplayValue);
    }
    [Fact]
    public async Task AiAndMarksTruncatedPagesPartialWithoutInventingCurrencyForEmptyLog()
    {
        using var handler = new Handler(_ => Ok("""{"data":[{"cost":"1","currency":"USD"}],"has_more":true,"next_after":"only-one-cursor"}"""));
        using var provider = new NativeProviders(handler);
        Assert.Equal(ReadingState.Partial, (await provider.FetchAsync("aiand", "fixture", _ => null, TestContext.Current.CancellationToken)).State);
        using var empty = new NativeProviders(new Handler(_ => Ok("""{"data":[],"has_more":false}""")));
        Assert.Empty((await empty.FetchAsync("aiand", "fixture", _ => null, TestContext.Current.CancellationToken)).Windows);
    }
    [Fact]
    public async Task LongCatActivePackPreventsLegacyQuotaMixing()
    {
        using var handler = new Handler(request =>
        {
            Assert.Null(request.Headers.Authorization); Assert.Equal("fixture=cookie", request.Headers.GetValues("Cookie").Single());
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/user-current" => Ok("""{"code":0,"data":{"name":"Fixture"}}"""),
                "/api/pay/quota/metering/token-packs/summary" => Pack(request),
                "/api/lc-platform/v1/pending-fuel-packages" => Ok("""{"code":0,"data":{"totalQuota":100,"list":[{"availableToken":25,"expireTime":1790812800000}]}}"""),
                _ => throw new InvalidOperationException("Active pack must not request the legacy quota")
            };
        });
        static HttpResponseMessage Pack(HttpRequestMessage request)
        { Assert.Equal(HttpMethod.Post, request.Method); return Ok("""{"code":0,"data":{"currentLot":{"status":"ACTIVE","totalToken":1000,"consumedToken":250}}}"""); }
        using var provider = new NativeProviders(handler);
        var reading = await provider.FetchAsync("longcat", "fixture=cookie", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal(75, reading.Windows[1].UsedPercent); Assert.All(reading.Windows, x => Assert.Equal("tokens", x.Unit));
        Assert.DoesNotContain("LONGCAT_API_KEY", NativeProviders.CredentialKeys("longcat")!);
    }
    [Fact]
    public async Task LongCatRejectsHttp200ExpiredSessionBeforeReadingQuota()
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(_ => { calls++; return Ok("""{"code":401,"data":{}}"""); }));
        Assert.Equal(ReadingState.NeedsAuth, (await provider.FetchAsync("longcat", "fixture", _ => null, TestContext.Current.CancellationToken)).State);
        Assert.Equal(1, calls);
    }
    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request)); }
}
