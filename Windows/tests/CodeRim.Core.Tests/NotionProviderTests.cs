using System.Net;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;
public sealed class NotionProviderTests
{
    [Fact]
    public async Task NotionResolvesPaidWorkspaceAndSendsMatchingScope()
    {
        using var provider = new NativeProviders(new Handler(async request =>
        {
            Assert.Null(request.Headers.Authorization); Assert.Equal("token_v2=fixture", request.Headers.GetValues("Cookie").Single());
            Assert.Equal(HttpMethod.Post, request.Method);
            if (request.RequestUri!.AbsolutePath.EndsWith("getSpaces", StringComparison.Ordinal))
                return Ok("""{"user":{"notion_user":{"user":{"value":{"id":"user"}}},"space":{"a":{"value":{"id":"a","subscription_tier":"free"}},"b":{"value":{"value":{"id":"b","name":"Team","subscription_tier":"business"}}}}}}""");
            Assert.Equal("user", request.Headers.GetValues("x-notion-active-user-header").Single());
            Assert.Equal("""{"spaceId":"b"}""", await request.Content!.ReadAsStringAsync());
            return Ok("""{"window":{"window":"6h","used":25,"limit":100,"scope":"user"},"resetsInSeconds":0,"billingPeriodWindow":{"used":50,"limit":100,"periodEndMs":1790812800000}}""");
        }));
        var reading = await provider.FetchAsync("notion", "fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal(360, reading.Headline.DurationMinutes);
        Assert.Equal(2, reading.Windows.Count); Assert.Equal("Team · business", reading.Plan);
        Assert.DoesNotContain("NOTION_API_KEY", NativeProviders.CredentialKeys("notion")!);
    }
    [Fact]
    public async Task NotionRejectsAmbiguousAccountAndUnavailableExplicitWorkspace()
    {
        var ambiguous = true; var calls = 0;
        using var provider = new NativeProviders(new Handler(request =>
        {
            calls++;
            return Task.FromResult(Ok(ambiguous ? """{"a":{"space":{}},"b":{"space":{}}}""" : """{"u":{"space":{"a":{"value":{"id":"a"}}}}}"""));
        }));
        Assert.Equal(ReadingState.Error, (await provider.FetchAsync("notion", "fixture", _ => null, TestContext.Current.CancellationToken)).State);
        ambiguous = false;
        Assert.Equal(ReadingState.Error, (await provider.FetchAsync("notion", "fixture", _ => "missing", TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void NoAllowanceClearsPreviouslyKnownQuota()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"status":"not_applicable"}""");
        var reading = NativeProviders.Parse("notion", new Dictionary<string, System.Text.Json.JsonElement> { ["main"] = document.RootElement.Clone() });
        var prior = new ProviderReading("notion", ReadingState.Ready, [new("rolling", "6h", 42.5)]);
        Assert.Empty(CodeRim.Core.Services.ReadingRetention.Merge(reading, prior).Windows);
        Assert.Contains("no AI allowance", reading.Message);
    }
    [Fact]
    public async Task MalformedUserIsIsolatedAndQuotedCookiesAreNormalized()
    {
        using var provider = new NativeProviders(new Handler(request =>
        {
            Assert.Equal("token_v2=fixture; device=id", request.Headers.GetValues("Cookie").Single());
            return Task.FromResult(Ok(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["bad" + (char)13 + (char)10 + "user"] = new { space = new { } } })));
        }));
        var reading = await provider.FetchAsync("notion", "Cookie: 'token_v2=fixture; device=id'", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Empty(reading.Windows);
    }
    private static HttpResponseMessage Ok(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request); }
}
