using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;
public sealed class ChutesProviderTests
{
    private static ProviderReading Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return NativeProviders.Parse("chutes", new Dictionary<string, JsonElement> { ["main"] = document.RootElement.Clone() });
    }
    [Fact]
    public void ChutesKeepsSeparateSubscriptionWindows()
    {
        var reading = Parse("""{"data":{"plan_name":"Pro","rolling_window":{"used":20,"limit":100,"reset_at":1790000000000},"monthly":{"percent_remaining":0.75}}}""");
        Assert.Equal(2, reading.Windows.Count); Assert.Equal(20, reading.Windows[0].UsedPercent);
        Assert.Equal(25, reading.Windows[1].UsedPercent); Assert.Equal(43200, reading.Windows[1].DurationMinutes);
        Assert.Equal("Pro", reading.Plan); Assert.NotNull(reading.Windows[0].ResetsAt);
    }
    [Fact]
    public void ChutesParsesMonthBeforeMinuteAndPreservesOnePercent()
    {
        var reading = Parse("""{"quotas":[{"name":"Generic","window":"1 month","used_percent":1}]}""");
        Assert.Equal("monthly", reading.Headline!.Id); Assert.Equal(43200, reading.Headline.DurationMinutes);
        Assert.Equal(1, reading.Headline.UsedPercent);
    }
    [Fact]
    public async Task ChutesEnrichesQuotaDefinitionsWithEscapedUsageIds()
    {
        var calls = new List<string>();
        using var provider = new NativeProviders(new Handler(request =>
        {
            calls.Add(request.RequestUri!.OriginalString);
            return request.RequestUri.AbsolutePath switch
            {
                "/users/me/subscription_usage" => Ok("{}"),
                "/users/me/quotas" => Ok("""[{"id":"a+b","name":"4-hour","limit":100,"window_hours":4}]"""),
                "/users/me/quota_usage/a%2Bb" => Ok("""{"used":25}"""),
                _ => throw new InvalidOperationException(request.RequestUri.OriginalString)
            };
        }));
        var reading = await provider.FetchAsync("chutes", "fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal(3, calls.Count);
    }
    [Fact]
    public async Task OptionalQuotaFailureKeepsPrimaryAndMarksPartial()
    {
        using var provider = new NativeProviders(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("subscription_usage", StringComparison.Ordinal)
            ? Ok("""{"rolling":{"used":25,"limit":100}}""") : new(HttpStatusCode.ServiceUnavailable)));
        var reading = await provider.FetchAsync("chutes", "fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Partial, reading.State); Assert.Equal(25, reading.Headline!.UsedPercent);
    }
    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request)); }
}
