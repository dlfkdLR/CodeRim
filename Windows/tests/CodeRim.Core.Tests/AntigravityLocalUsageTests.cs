using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;
public sealed class AntigravityLocalUsageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private const string Summary = """{"groups":[{"displayName":"Model","buckets":[{"bucketId":"session","remainingFraction":0.25},{"bucketId":"weekly","remaining":{"case":"remainingFraction","value":1}}]}]}""";
    private static JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
    [Theory]
    [InlineData("")]
    [InlineData("response")]
    [InlineData("summary")]
    public void ReadsPinnedSummaryEnvelopesAndIndependentWindows(string envelope)
    {
        var root = Json(envelope.Length == 0 ? Summary : "{\"" + envelope + "\":" + Summary + "}");
        var reading = AntigravityLocalUsage.ParseSummary(root, Now);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(2, reading.Windows.Count);
        Assert.Equal(75, reading.Windows[0].UsedPercent); Assert.Equal(300, reading.Windows[0].DurationMinutes);
        Assert.Equal(0, reading.Windows[1].UsedPercent); Assert.Equal(10080, reading.Windows[1].DurationMinutes);
        Assert.All(reading.Windows, x => { Assert.Null(x.Unit); Assert.Null(x.UsedCount); });
    }
    [Theory]
    [InlineData("null")]
    [InlineData("-0.1")]
    [InlineData("1.1")]
    [InlineData("\"0.5\"")]
    public void UnknownFractionDoesNotBecomeAvailableQuota(string value)
    {
        var reading = AntigravityLocalUsage.ParseSummary(Json("{\"groups\":[{\"buckets\":[{\"bucketId\":\"unknown\",\"remainingFraction\":" + value + "}]}]}"), Now);
        Assert.Equal(ReadingState.Unavailable, reading.State); Assert.Empty(reading.Windows);
    }
    [Fact]
    public void DisabledAndUnknownBucketsKeepIndependentQuality()
    {
        var reading = AntigravityLocalUsage.ParseSummary(Json("""{"groups":[{"buckets":[{"bucketId":"off","disabled":true,"remainingFraction":1},{"bucketId":"known","remainingFraction":0},{"bucketId":"unknown"}]}]}"""), Now);
        Assert.Equal(ReadingState.Partial, reading.State); Assert.Single(reading.Windows);
        Assert.Equal(100, reading.Headline!.UsedPercent); Assert.Equal(0, reading.Headline.DurationMinutes);
    }
    [Fact]
    public void DuplicateFieldsAndBucketIdentitiesAreRejected()
    {
        Assert.Equal(ReadingState.Error, AntigravityLocalUsage.ParseSummary(Json("""{"groups":[],"groups":[]}"""), Now).State);
        Assert.Equal(ReadingState.Error, AntigravityLocalUsage.ParseSummary(Json("""{"groups":[{"buckets":[{"bucketId":"x","remainingFraction":0.2},{"bucketId":"x","remainingFraction":0.9}]}]}"""), Now).State);
    }
    [Fact]
    public void LegacyModelsKeepReportedIdentityAndPlan()
    {
        var reading = AntigravityLocalUsage.ParseLegacy(Json("""{"userStatus":{"userTier":{"name":"Pro"},"cascadeModelConfigData":{"clientModelConfigs":[{"label":"Model A","modelOrAlias":{"model":"a"},"quotaInfo":{"remainingFraction":0.75}},{"label":"Model B","modelOrAlias":{"model":"b"},"quotaInfo":{}}]}}}"""), Now);
        Assert.Equal(ReadingState.Partial, reading.State); Assert.Equal("Pro", reading.Plan);
        Assert.Equal(25, Assert.Single(reading.Windows).UsedPercent); Assert.Contains("/a", reading.Headline!.Id, StringComparison.Ordinal);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private static HttpResponseMessage Ok(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    [Fact]
    public async Task SendsOnlyFixedReadOnlyLocalProtocol()
    {
        var calls = 0; using var client = new HttpClient(new Handler(async (request, token) =>
        {
            calls++; Assert.Equal("127.0.0.1", request.RequestUri!.Host); Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("local-fixture", Assert.Single(request.Headers.GetValues("X-Codeium-Csrf-Token")));
            Assert.Equal("1", Assert.Single(request.Headers.GetValues("Connect-Protocol-Version")));
            if (request.RequestUri.AbsolutePath.EndsWith("/GetUserStatus", StringComparison.Ordinal)) return Ok("""{"userStatus":{"userTier":{"name":"Pro"}}}""");
            Assert.EndsWith("/RetrieveUserQuotaSummary", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal("""{"forceRefresh":true}""", await request.Content!.ReadAsStringAsync(token)); return Ok(Summary);
        })) { BaseAddress = new("https://127.0.0.1:12345/") };
        var reading = await AntigravityLocalUsage.FetchAsync(client, "local-fixture", () => true, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(2, calls); Assert.Equal("Pro", reading.Plan);
    }
    [Fact]
    public async Task LegacyFallbackStaysOnSameVerifiedProcess()
    {
        var paths = new List<string>(); using var client = new HttpClient(new Handler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(paths.Count == 1 ? new(HttpStatusCode.NotFound) : paths.Count == 2 ? Ok("""{"userStatus":{}}""")
                : Ok("""{"clientModelConfigs":[{"label":"Legacy","modelOrAlias":{"model":"legacy"},"quotaInfo":{"remainingFraction":0.3}}]}"""));
        })) { BaseAddress = new("https://127.0.0.1:12345/") };
        var reading = await AntigravityLocalUsage.FetchAsync(client, "fixture", () => true, TestContext.Current.CancellationToken);
        Assert.Equal(70, reading.Headline!.UsedPercent);
        Assert.Collection(paths, p => Assert.EndsWith("/RetrieveUserQuotaSummary", p, StringComparison.Ordinal),
            p => Assert.EndsWith("/GetUserStatus", p, StringComparison.Ordinal), p => Assert.EndsWith("/GetCommandModelConfigs", p, StringComparison.Ordinal));
    }
    [Theory]
    [InlineData(401, ReadingState.NeedsAuth)]
    [InlineData(403, ReadingState.NeedsAuth)]
    [InlineData(429, ReadingState.Unavailable)]
    public async Task AuthenticationAndRateLimitStopFallbacks(int status, ReadingState expected)
    {
        var calls = 0; using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)); }))
        { BaseAddress = new("https://127.0.0.1:12345/") };
        var reading = await AntigravityLocalUsage.FetchAsync(client, "fixture", () => true, TestContext.Current.CancellationToken);
        Assert.Equal(expected, reading.State); Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData("http://127.0.0.1:12345/")]
    [InlineData("https://localhost:12345/")]
    [InlineData("https://127.0.0.1:12345/other")]
    [InlineData("https://127.0.0.1:12345/?query=true")]
    [InlineData("https://external.invalid/")]
    public async Task RejectsNonPinnedBaseWithoutRequests(string uri)
    {
        var calls = 0; using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Ok(Summary)); })) { BaseAddress = new(uri) };
        Assert.Equal(ReadingState.Error, (await AntigravityLocalUsage.FetchAsync(client, "fixture", () => true, TestContext.Current.CancellationToken)).State);
        Assert.Equal(0, calls);
    }
    [Fact]
    public async Task ProcessChangeAndCancellationCannotPublishReading()
    {
        var live = true; using var client = new HttpClient(new Handler((_, _) => { live = false; return Task.FromResult(Ok(Summary)); })) { BaseAddress = new("https://127.0.0.1:12345/") };
        Assert.Equal(ReadingState.Unavailable, (await AntigravityLocalUsage.FetchAsync(client, "fixture", () => live, TestContext.Current.CancellationToken)).State);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AntigravityLocalUsage.FetchAsync(client, "fixture", () => true, cancel.Token));
    }
    [Theory]
    [InlineData("gemini-weekly", "", 10080)]
    [InlineData("3p-5h", "", 300)]
    [InlineData("unknown", "Weekly limit", 10080)]
    [InlineData("unknown", "five hour", 300)]
    [InlineData("gemini_weekly", "", 10080)]
    [InlineData("weeklyish", "unknown", 0)]
    public void PinnedFamilyCadencesUseIdsAndLabels(string id, string label, int expected)
    {
        var root = JsonSerializer.SerializeToElement(new { groups = new[] { new { buckets = new[] { new { bucketId = id, displayName = label, remainingFraction = 0.5 } } } } });
        Assert.Equal(expected, Assert.Single(AntigravityLocalUsage.ParseSummary(root, Now).Windows).DurationMinutes);
    }
    [Theory]
    [InlineData("404")]
    [InlineData("malformed")]
    [InlineData("network")]
    [InlineData("invalid")]
    public async Task EarlierUnavailableEndpointsDoNotBlockLegacyModels(string failure)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            if (calls == 3) return Task.FromResult(Ok("""{"clientModelConfigs":[{"label":"Legacy","modelOrAlias":{"model":"legacy"},"quotaInfo":{"remainingFraction":0.3}}]}"""));
            if (failure == "network") throw new HttpRequestException();
            return Task.FromResult(failure == "404" ? new(HttpStatusCode.NotFound) : Ok(failure == "malformed" ? "{" : """{"groups":[],"groups":[]}"""));
        })) { BaseAddress = new("https://127.0.0.1:12345/") };
        var reading = await AntigravityLocalUsage.FetchAsync(client, "fixture", () => true, TestContext.Current.CancellationToken);
        Assert.Equal(70, reading.Headline!.UsedPercent); Assert.Equal(3, calls);
    }
    [Theory]
    [InlineData("16", ReadingState.NeedsAuth)]
    [InlineData("\"unauthenticated\"", ReadingState.NeedsAuth)]
    [InlineData("8", ReadingState.Unavailable)]
    public async Task SemanticAuthenticationAndThrottleStopRequests(string code, ReadingState expected)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Ok("{\"code\":" + code + "}")); }))
        { BaseAddress = new("https://127.0.0.1:12345/") };
        Assert.Equal(expected, (await AntigravityLocalUsage.FetchAsync(client, "fixture", () => true, TestContext.Current.CancellationToken)).State);
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task SlowSummaryLeavesTimeForLegacyAndCallerCancellationStillPropagates()
    {
        var calls = 0; var elapsed = System.Diagnostics.Stopwatch.StartNew();
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            if (++calls == 1) await Task.Delay(Timeout.Infinite, token);
            return Ok("""{"userStatus":{"cascadeModelConfigData":{"clientModelConfigs":[{"label":"Legacy","modelOrAlias":{"model":"legacy"},"quotaInfo":{"remainingFraction":0.3}}]}}}""");
        })) { BaseAddress = new("https://127.0.0.1:12345/") };
        var result = await AntigravityLocalUsage.FetchAsync(client, "fixture", () => true, TestContext.Current.CancellationToken);
        Assert.Equal(70, result.Headline!.UsedPercent); Assert.Equal(2, calls); Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(7));
    }

}
