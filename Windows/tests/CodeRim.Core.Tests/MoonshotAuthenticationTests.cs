using CodeRim.Core.Providers;
using CodeRim.Core.Domain;
using System.Net;
using System.Text.Json;
namespace CodeRim.Core.Tests;

public sealed class MoonshotAuthenticationTests
{
    [Theory]
    [InlineData(null, "international")]
    [InlineData(" china ", "china")]
    [InlineData("\"CHINA\"", "china")]
    [InlineData("global", null)]
    public void ParsesOnlyNamedRegions(string? value, string? expected) => Assert.Equal(expected, MoonshotAuthentication.Region(value));

    [Fact]
    public void KeepsSavedAndEnvironmentCredentialsInTheirOwnRegion()
    {
        var saved = new Dictionary<string, string?> { ["provider:moonshot"] = "old-ai" };
        var environment = new Dictionary<string, string?> { ["MOONSHOT_API_KEY"] = "env-ai" };
        string? Get(string region) => MoonshotAuthentication.Credential(region, key => saved.GetValueOrDefault(key), key => environment.GetValueOrDefault(key));
        Assert.Equal("old-ai", Get("international")); Assert.Null(Get("china"));
        environment["MOONSHOT_REGION"] = "china"; Assert.Equal("env-ai", Get("china"));
        saved["provider:moonshot:china"] = "saved-cn"; Assert.Equal("saved-cn", Get("china")); Assert.Equal("old-ai", Get("international"));
        environment["CODEXBAR_MOONSHOT_API_KEY_REGION"] = "international"; environment["CODEXBAR_MOONSHOT_API_KEY"] = "bound-ai";
        saved.Clear(); Assert.Equal("bound-ai", Get("international")); Assert.Equal("env-ai", Get("china"));
        environment.Remove("MOONSHOT_API_KEY"); environment["MOONSHOT_KEY"] = "'alias-cn'"; Assert.Equal("alias-cn", Get("china"));
        Assert.Null(Get("other"));
    }
    [Fact]
    public void DoesNotTreatUnboundConfiguredKeyAsInternational()
    {
        Assert.Null(MoonshotAuthentication.Credential("international", _ => null, key => key == "CODEXBAR_MOONSHOT_API_KEY" ? "unbound" : null));
        Assert.Equal("https://api.moonshot.cn/v1/users/me/balance", MoonshotAuthentication.Endpoint("china"));
        Assert.Throws<ArgumentException>(() => MoonshotAuthentication.Endpoint("https://external.invalid"));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(send(request)); }
    }
    [Theory]
    [InlineData("china", "api.moonshot.cn")]
    [InlineData("international", "api.moonshot.ai")]
    [InlineData(null, "api.moonshot.ai")]
    [InlineData("\"CHINA\"", "api.moonshot.cn")]
    public async Task RoutesBalanceRequestsToTheSelectedPinnedRegion(string? region, string host)
    {
        var calls = 0;
        using var provider = new HttpProviders(new Handler(request =>
        {
            calls++; Assert.Equal(host, request.RequestUri!.Host);
            Assert.Equal("/v1/users/me/balance", request.RequestUri.AbsolutePath);
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("synthetic-region-key", request.Headers.Authorization?.Parameter);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"code":0,"status":true,"scode":"ok","data":{"available_balance":12.5,"voucher_balance":2.5,"cash_balance":10}}""") };
        }));
        var result = await provider.FetchAsync("moonshot", "synthetic-region-key", key => key == "MOONSHOT_REGION" ? region : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, result.State); Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData("https://external.invalid", "synthetic-key", ReadingState.Error)]
    [InlineData("china", "invalid\r\nheader", ReadingState.NeedsAuth)]
    [InlineData("china", "", ReadingState.NeedsAuth)]
    public async Task RejectsInvalidRegionAndCredentialsWithoutRequests(string region, string credential, ReadingState expected)
    {
        var calls = 0;
        using var provider = new HttpProviders(new Handler(_ => { calls++; throw new InvalidOperationException(); }));
        var result = await provider.FetchAsync("moonshot", credential, _ => region, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("''")]
    [InlineData(" \t ")]
    public void ConfiguredKeyRequiresAnExplicitRegion(string emptyRegion)
    {
        Assert.Null(MoonshotAuthentication.Credential("international", _ => null,
            key => key == "CODEXBAR_MOONSHOT_API_KEY" ? "unbound" : key == "CODEXBAR_MOONSHOT_API_KEY_REGION" ? emptyRegion : null));
    }
    [Theory]
    [InlineData("china", "CNY")]
    [InlineData("international", "USD")]
    public void PreservesCurrencyAndNegativeCash(string region, string currency)
    {
        using var json = JsonDocument.Parse("""{"code":0,"status":true,"scode":"ok","data":{"available_balance":12.5,"voucher_balance":15,"cash_balance":-2.5}}""");
        var reading = HttpProviders.ParseMoonshot(json.RootElement, region);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(2, reading.Windows.Count);
        Assert.All(reading.Windows, window => { Assert.Equal(currency, window.Unit); Assert.Null(window.UsedPercent); Assert.Null(window.UsedCount); });
        Assert.Contains("in deficit", reading.Windows[1].DisplayValue, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("""{"code":401,"status":true,"scode":"err","data":{"available_balance":12.5,"voucher_balance":2.5,"cash_balance":10}}""")]
    [InlineData("""{"code":0,"status":false,"scode":"err","data":{"available_balance":12.5,"voucher_balance":2.5,"cash_balance":10}}""")]
    [InlineData("""{"code":401,"code":0,"status":true,"scode":"ok","data":{"available_balance":12.5,"voucher_balance":2.5,"cash_balance":10}}""")]
    [InlineData("""{"code":0,"status":true,"scode":"ok","data":{"available_balance":12.5}}""")]
    [InlineData("""{"code":0,"status":true,"scode":"ok","data":{"available_balance":"12.5","voucher_balance":2.5,"cash_balance":10}}""")]
    public void RejectsFailedAmbiguousAndIncompleteBalances(string response)
    {
        using var json = JsonDocument.Parse(response); var reading = HttpProviders.ParseMoonshot(json.RootElement, "china");
        Assert.Equal(ReadingState.Error, reading.State); Assert.Empty(reading.Windows);
    }

}
