using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;
public sealed class MetricsProviderTests
{
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    [Fact]
    public async Task GroqRetainsRatesWithoutInventingQuota()
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(request =>
        {
            calls++;
            Assert.Equal("Bearer fixture", request.Headers.Authorization!.ToString());
            Assert.Equal("/v1/metrics/prometheus/api/v1/query", request.RequestUri!.AbsolutePath);
            return Ok("""{"status":"success","data":{"result":[{"value":[1790000000,"0.5"]},{"value":[1790000000,1.5]}]}}""");
        }));
        var reading = await provider.FetchAsync("groq", "fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(4, calls); Assert.Equal(3, reading.Windows.Count); Assert.All(reading.Windows, x => Assert.Null(x.UsedPercent));
        Assert.StartsWith("120", reading.Windows[0].DisplayValue); Assert.StartsWith("240", reading.Windows[1].DisplayValue);
    }
    [Fact]
    public void GroqRejectsNonFiniteAndNegativeMetrics()
    {
        var payload = Json("""{"status":"success","data":{"result":[{"value":[1790000000,"-1"]}]}}""");
        Assert.Throws<InvalidDataException>(() => NativeProviders.Parse("groq", new Dictionary<string, JsonElement> { ["requests"] = payload }));
    }
    [Theory]
    [InlineData("100", 25d)]
    [InlineData("{\"limited\":100}", 25d)]
    [InlineData("\"unlimited\"", null)]
    public void ZedSupportsLegacyAndCurrentLimits(string limit, double? percent)
    {
        var payload = Json("""{"plan":{"plan_v3":"zed_pro","usage":{"edit_predictions":{"used":25,"limit":LIMIT}}}}""".Replace("LIMIT", limit, StringComparison.Ordinal));
        var reading = NativeProviders.Parse("zed", new Dictionary<string, JsonElement> { ["main"] = payload });
        Assert.Equal(percent, reading.Headline!.UsedPercent); Assert.Equal(25, reading.Headline.UsedCount); Assert.Equal("predictions", reading.Headline.Unit);
    }
    [Fact]
    public async Task ZedUsesItsOwnAuthorizationFormatAndChecksUser()
    {
        using var provider = new NativeProviders(new Handler(request =>
        {
            Assert.Equal("123 fixture", request.Headers.GetValues("Authorization").Single());
            return Ok("""{"user":{"id":456},"plan":{"usage":{"edit_predictions":{"used":25,"limit":100}}}}""");
        }));
        var reading = await provider.FetchAsync("zed", "fixture", _ => "123", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Empty(reading.Windows);
    }
    private static HttpResponseMessage Ok(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request)); }
}
