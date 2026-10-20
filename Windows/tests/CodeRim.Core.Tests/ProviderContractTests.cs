using System.Net;
using System.Text.Json;
using System.Xml;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;

public sealed class ProviderContractTests
{
    private static ProviderReading Parse(string id, string json)
    { using var document = JsonDocument.Parse(json); return NativeProviders.Parse(id, new Dictionary<string, JsonElement> { ["main"] = document.RootElement }); }
    [Fact]
    public void CodebuffAcceptsDocumentedNumericStrings()
    {
        var value = Parse("codebuff", """{"usage":"12","quota":"100","remainingBalance":"88"}""");
        Assert.Equal(12, value.Headline!.UsedPercent); Assert.StartsWith("88", value.Windows[1].DisplayValue);
    }
    [Fact]
    public async Task CodebuffPostsUsageAndKeepsItWhenOptionalSubscriptionIsMalformed()
    {
        using var handler = new MockHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/usage")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
                Assert.Equal("codexbar-usage", body.RootElement.GetProperty("fingerprintId").GetString());
                return """{"usage":"12","quota":"100","remainingBalance":"88"}""";
            }
            Assert.Equal(HttpMethod.Get, request.Method); return "invalid JSON";
        });
        using var provider = new NativeProviders(handler);
        var result = await provider.FetchAsync("codebuff", "fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Partial, result.State); Assert.Equal(12, result.Headline!.UsedPercent);
    }
    [Fact]
    public void NeuralwattUsesSubscriptionEnergyInsteadOfPrepaidBalance()
    {
        var value = Parse("neuralwatt", """{"balance":{"credits_remaining_usd":0},"subscription":{"kwh_used":2.5,"kwh_included":10,"current_period_end":"2026-10-01T00:00:00Z"},"key":{"allowance":{"blocked":true}}}""");
        Assert.Equal(25, value.Headline!.UsedPercent); Assert.Equal("kWh", value.Headline.Unit);
        Assert.NotNull(value.Headline.ResetsAt); Assert.Equal(100, value.Windows.Single(x => x.Id == "key").UsedPercent);
        Assert.Null(value.Windows.Single(x => x.Id == "balance").UsedPercent);
    }
    [Fact]
    public async Task FireworksAcceptsDotsAndUnderscoresInSlug()
    {
        using var handler = new MockHandler(request =>
        { Assert.Contains("/accounts/acct-1_x.d/billing/summary", request.RequestUri!.AbsolutePath); return Task.FromResult("""{"lineItems":[]}"""); });
        using var provider = new NativeProviders(handler);
        var result = await provider.FetchAsync("fireworks", "fixture", _ => "acct-1_x.d", TestContext.Current.CancellationToken);
        Assert.NotEqual(ReadingState.NeedsAuth, result.State); Assert.Equal(1, handler.Requests);
    }
    [Fact]
    public void ZenmuxFractionRetainsFlowUnit()
    {
        var value = Parse("zenmux", """{"success":true,"data":{"quota_5_hour":{"usage_percentage":0.25,"used_flows":5,"max_flows":20},"quota_7_day":{"usage_percentage":0}}}""");
        Assert.Equal(25, value.Headline!.UsedPercent); Assert.Equal("flows", value.Headline.Unit); Assert.Equal(0, value.Windows[1].UsedPercent);
    }
    [Fact]
    public void LlmProxyAcceptsKeyedQuotasAndDoesNotCallRequestsTokens()
    {
        var value = Parse("llmproxy", """{"providers":{"codex":{"quota_groups":{"weekly":{"remaining_percent":25}},"total_requests":5}}}""");
        Assert.Equal(75, value.Headline!.UsedPercent); Assert.Equal("requests", value.Windows[1].Unit); Assert.Equal(5, value.Windows[1].UsedCount);
    }
    [Fact]
    public async Task LiteLlmRejectsAnotherUsersResponse()
    {
        using var handler = new MockHandler(request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/key/info", StringComparison.Ordinal)
            ? """{"info":{"user_id":"expected","spend":2}}""" : """{"user_info":{"user_id":"another","spend":900}}"""));
        using var provider = new NativeProviders(handler);
        var result = await provider.FetchAsync("litellm", "fixture", _ => "https://proxy.example.invalid/v1", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, result.State); Assert.Empty(result.Windows);
    }
    [Theory]
    [InlineData("http://remote.example.invalid")]
    [InlineData("https://user:password@proxy.example.invalid")]
    [InlineData("https://proxy.example.invalid?key=secret")]
    [InlineData("file:///tmp/example")]
    public void CustomEndpointRejectsCredentialsAndInsecureRemote(string value) => Assert.Throws<InvalidDataException>(() => NativeProviders.ManagementBase(value));
    [Theory]
    [InlineData("http://127.0.0.1:4000/v1")]
    [InlineData("https://proxy.example.invalid/v1")]
    public void CustomEndpointKeepsExplicitOrigin(string value) => Assert.Equal(value, NativeProviders.ManagementBase(value));
    [Fact]
    public async Task WarpUsesWindowsGraphqlAndUnlimitedHasNoPercentage()
    {
        using var handler = new MockHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("Windows", request.Headers.GetValues("x-warp-os-category").Single());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("GetRequestLimitInfo", body.RootElement.GetProperty("operationName").GetString());
            return """{"data":{"user":{"user":{"requestLimitInfo":{"isUnlimited":true,"requestLimit":100,"requestsUsedSinceLastRefresh":20}}}}}""";
        });
        using var provider = new NativeProviders(handler);
        var value = await provider.FetchAsync("warp", "fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Null(value.Headline!.UsedPercent); Assert.Equal(20, value.Headline.UsedCount);
    }
    [Fact]
    public void JetbrainsParsesEscapedQuotaAndRejectsExternalEntities()
    {
        const string xml = """<application><component name="AIAssistantQuotaManager2"><option name="quotaInfo" value="{&quot;current&quot;:&quot;2.5&quot;,&quot;maximum&quot;:&quot;10&quot;}"/></component></application>""";
        var value = JetBrainsQuota.Parse(xml, DateTimeOffset.UtcNow); Assert.Equal(25, value.Headline!.UsedPercent);
        Assert.Empty(JetBrainsQuota.Parse(xml.Replace("&quot;10&quot;", "&quot;0&quot;", StringComparison.Ordinal), DateTimeOffset.UtcNow).Windows);
        Assert.Throws<XmlException>(() => JetBrainsQuota.Parse("""<!DOCTYPE doc [<!ENTITY x SYSTEM "file:///secret">]><application>&x;</application>""", DateTimeOffset.UtcNow));
    }
    [Fact]
    public async Task StandardEnvironmentNamesSelectConfiguredEndpoints()
    {
        foreach (var (id, key, url) in new[] { ("llmproxy", "LLM_PROXY_BASE_URL", "https://proxy.example.invalid"), ("wayfinder", "WAYFINDER_GATEWAY_URL", "http://127.0.0.1:9000") })
        {
            Assert.Contains(NativeProviders.Settings(id), x => x.Key == key);
            using var handler = new MockHandler(request =>
            {
                Assert.StartsWith(url, request.RequestUri!.AbsoluteUri);
                return Task.FromResult(id == "llmproxy" ? """{"providers":{}}""" : request.RequestUri.AbsolutePath == "/healthz" ? """{"status":"ok"}""" : "{}");
            });
            using var provider = new NativeProviders(handler);
            var result = await provider.FetchAsync(id, "fixture", field => field == key ? url : null, TestContext.Current.CancellationToken);
            Assert.Equal(ReadingState.Ready, result.State); Assert.True(handler.Requests > 0);
        }
    }
    [Fact]
    public void JetbrainsKeepsQuotaWhenOptionalRefillIsMalformed()
    {
        var value = JetBrainsQuota.Parse("""<application><component name="AIAssistantQuotaManager2"><option name="quotaInfo" value="{&quot;current&quot;:&quot;2.5&quot;,&quot;maximum&quot;:&quot;10&quot;}"/><option name="nextRefill" value="not JSON"/></component></application>""", DateTimeOffset.UtcNow);
        Assert.Equal(25, value.Headline!.UsedPercent); Assert.Null(value.Headline.ResetsAt);
    }
    private sealed class MockHandler(Func<HttpRequestMessage, Task<string>> body) : HttpMessageHandler
    {
        internal int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return new(HttpStatusCode.OK) { Content = new StringContent(await body(request)) }; }
    }
}
