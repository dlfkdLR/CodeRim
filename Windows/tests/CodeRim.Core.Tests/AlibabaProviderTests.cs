using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;
public sealed class AlibabaProviderTests
{
    private static ProviderReading Parse(string value) { using var document = JsonDocument.Parse(value); return NativeProviders.Parse("alibaba", new Dictionary<string, JsonElement> { ["main"] = document.RootElement.Clone() }); }
    [Fact]
    public void AlibabaSelectsActiveInstanceWithoutBorrowingOtherQuota()
    {
        var reading = Parse("""{"data":{"codingPlanInstanceInfos":[{"status":"EXPIRED","codingPlanQuotaInfo":{"per5HourUsedQuota":99,"per5HourTotalQuota":100}},{"status":"ACTIVE","planName":"Pro","codingPlanQuotaInfo":{"per5HourUsedQuota":25,"per5HourTotalQuota":100,"perWeekUsedQuota":10,"perWeekTotalQuota":100}}]}}""");
        Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal(2, reading.Windows.Count); Assert.Equal("Pro", reading.Plan);
        reading = Parse("""{"codingPlanInstanceInfos":[{"status":"EXPIRED","codingPlanQuotaInfo":{"per5HourUsedQuota":99,"per5HourTotalQuota":100}},{"status":"ACTIVE","planName":"Pro"}]}""");
        Assert.Empty(reading.Windows); Assert.Contains("active", reading.Message);
    }
    [Fact]
    public void AlibabaExpandsEncodedResponseAndDoesNotDefaultMissingUsageToZero()
    {
        var reading = Parse("""{"data":"{\"codingPlanQuotaInfo\":{\"per5HourTotalQuota\":100,\"perWeekUsedQuota\":20,\"perWeekTotalQuota\":100}}"}""");
        Assert.Single(reading.Windows); Assert.Equal(20, reading.Headline!.UsedPercent); Assert.Equal("weekly", reading.Headline.Id);
    }
    [Fact]
    public async Task AlibabaUsesRegionalCodingPlanRequest()
    {
        using var provider = new NativeProviders(new Handler(async request =>
        {
            Assert.Equal("bailian.console.aliyun.com", request.RequestUri!.Host); Assert.Contains("cn-beijing", request.RequestUri.Query);
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Contains("sfm_codingplan_public_cn", await request.Content!.ReadAsStringAsync());
            Assert.Equal("Bearer fixture", request.Headers.Authorization!.ToString()); Assert.Equal("fixture", request.Headers.GetValues("X-DashScope-API-Key").Single());
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"codingPlanQuotaInfo":{"per5HourUsedQuota":0,"per5HourTotalQuota":100}}""") };
        }));
        Assert.Equal(0, (await provider.FetchAsync("alibaba", "fixture", _ => "cn", TestContext.Current.CancellationToken)).Headline!.UsedPercent);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request); }
}
