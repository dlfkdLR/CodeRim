using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;

public sealed class DeepSeekConnectionTests
{
    private const string Platform = """{"code":0,"data":{"biz_code":0,"biz_data":{"normal_wallets":[{"currency":"USD","balance":"10.25"},{"currency":"CNY","balance":8}],"bonus_wallets":[{"currency":"USD","balance":"2.25"}]}}}""";
    [Theory]
    [InlineData("DEEPSEEK_PLATFORM_TOKEN")]
    [InlineData("DEEPSEEK_USER_TOKEN")]
    public void PlatformAliasesCannotBecomeApiKeys(string name)
    {
        var selected = DeepSeekAuthentication.Resolve(null, _ => null, key => key == name ? "platform-token" : null);
        Assert.Equal(new DeepSeekCredential("web", "platform-token"), selected);
        Assert.Equal(new DeepSeekCredential("api", null), DeepSeekAuthentication.Resolve("api", _ => null, key => key == name ? "platform-token" : null));
    }
    [Theory]
    [InlineData("api", "provider:deepseek")]
    [InlineData("auto", "provider:deepseek")]
    [InlineData("web", "provider:deepseek:web")]
    public void UnselectedEncryptedSlotsAreNeverRead(string source, string selected)
    {
        string? Saved(string key) => key == selected ? "working-credential"
            : throw new System.Security.Cryptography.CryptographicException("Unrelated damaged slot");
        var credential = DeepSeekAuthentication.Resolve(source, Saved, _ => null);
        Assert.Equal(new DeepSeekCredential(source == "web" ? "web" : "api", "working-credential"), credential);
    }
    [Fact]
    public void UnavailableApiBalanceKeepsMoneyWithoutClaimingItCanBeUsed()
    {
        using var document = JsonDocument.Parse("""{"is_available":false,"balance_infos":[{"currency":"USD","total_balance":"12.50"}]}""");
        var reading = DeepSeekBalance.Parse(document.RootElement, "api");
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal("Total balance", reading.Headline!.Name);
        Assert.Contains("12", reading.Headline.DisplayValue);
        Assert.Equal("Balance unavailable for API calls.", reading.Message);
        Assert.Null(reading.Headline.UsedPercent);
    }
    [Fact]
    public void ApiAndWebSecretsRemainSeparateAndBlankAliasesDoNotShadow()
    {
        string? Saved(string key) => key switch { "provider:deepseek" => "saved-api", "provider:deepseek:web" => "saved-web", _ => null };
        Assert.Equal(new DeepSeekCredential("api", "saved-api"), DeepSeekAuthentication.Resolve("auto", Saved, _ => "environment"));
        Assert.Equal(new DeepSeekCredential("web", "saved-web"), DeepSeekAuthentication.Resolve("web", Saved, _ => "environment"));
        Assert.Equal(new DeepSeekCredential("api", "api-alias"), DeepSeekAuthentication.Resolve("auto", _ => " ", key => key == "DEEPSEEK_KEY" ? "api-alias" : " "));
        Assert.Null(DeepSeekAuthentication.Resolve("untrusted", Saved, _ => null));
    }
    [Theory]
    [InlineData("api", "https://api.deepseek.com/user/balance", """{"is_available":true,"balance_infos":[{"currency":"USD","total_balance":"12.50","granted_balance":"2.25","topped_up_balance":"10.25"}]}""")]
    [InlineData("web", "https://platform.deepseek.com/api/v0/users/get_user_summary", Platform)]
    public async Task EachCredentialReachesOnlyItsOwnFixedEndpoint(string source, string endpoint, string body)
    {
        using var provider = new HttpProviders(new Handler(request => {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal(endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer " + source + "-credential", request.Headers.Authorization!.ToString());
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal(source == "web", request.Headers.Contains("x-client-platform"));
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var reading = await provider.FetchAsync("deepseek", source + "-credential", _ => source, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal("USD", reading.Headline!.Unit);
        Assert.Null(reading.Headline.UsedPercent); Assert.Null(reading.Headline.UsedCount);
        Assert.Contains("12", reading.Headline.DisplayValue);
    }
    [Theory]
    [InlineData("""{"code":40002,"data":[]}""")]
    [InlineData("""{"code":40003,"data":"expired"}""")]
    [InlineData("""{"code":0,"data":{"biz_code":40002,"biz_data":null}}""")]
    public void PlatformSemanticAuthErrorsDoNotBecomeZeroBalance(string body)
    {
        using var document = JsonDocument.Parse(body);
        var reading = DeepSeekBalance.Parse(document.RootElement, "web");
        Assert.Equal(ReadingState.NeedsAuth, reading.State); Assert.Empty(reading.Windows);
    }
    [Theory]
    [InlineData("""{"code":0,"data":{"biz_code":5,"biz_data":{}}}""", "web")]
    [InlineData("""{"code":0,"code":0,"data":{}}""", "web")]
    [InlineData("""{"data":{"biz_data":{"normal_wallets":[{"currency":"USD","balance":"NaN"}],"bonus_wallets":[]}}}""", "web")]
    [InlineData("""{"data":{"biz_data":{"normal_wallets":[{"currency":"USD","balance":"1e100"}],"bonus_wallets":[]}}}""", "web")]
    [InlineData("""{"data":{"biz_data":{"normal_wallets":[{"currency":"USD","balance":1,"balance":2}],"bonus_wallets":[]}}}""", "web")]
    [InlineData("""{"data":{"biz_data":{"normal_wallets":[]}}}""", "web")]
    [InlineData("""{"balance_infos":[{"currency":"USD","total_balance":"NaN"}]}""", "api")]
    [InlineData("""{"balance_infos":[{"currency":"USD","total_balance":"12","granted_balance":null}]}""", "api")]
    [InlineData("""{"balance_infos":[{"currency":"USD","total_balance":"1"},{"currency":"USD","total_balance":"2"}]}""", "api")]
    [InlineData("""{"balance_infos":[{"currency":"USD\n","total_balance":"1"}]}""", "api")]
    public void AmbiguousMalformedOrNonFiniteMoneyIsRejected(string body, string source)
    {
        using var document = JsonDocument.Parse(body);
        var reading = DeepSeekBalance.Parse(document.RootElement, source);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Empty(reading.Windows);
    }
    [Fact]
    public void WalletsSumByCurrencyWithoutConvertingMoneyToTokens()
    {
        using var document = JsonDocument.Parse(Platform);
        var reading = DeepSeekBalance.Parse(document.RootElement, "web");
        Assert.Collection(reading.Windows, window => Assert.Equal("USD", window.Unit), window => Assert.Equal("CNY", window.Unit));
        Assert.All(reading.Windows, window => { Assert.Null(window.UsedCount); Assert.Null(window.UsedPercent); Assert.Null(window.ResetsAt); });
        Assert.Contains("12", reading.Windows[0].DisplayValue); Assert.Contains("8", reading.Windows[1].DisplayValue);
    }
    [Fact]
    public void PaidAndGrantedBalancesStayInTheirOwnCurrency()
    {
        using var document = JsonDocument.Parse(Platform);
        var reading = DeepSeekBalance.Parse(document.RootElement, "web");
        var usd = reading.Windows.Single(row => row.Unit == "USD").DisplayValue!;
        var cny = reading.Windows.Single(row => row.Unit == "CNY").DisplayValue!;
        Assert.Contains("Paid: " + 10.25m.ToString("N2", System.Globalization.CultureInfo.CurrentCulture) + " USD", usd);
        Assert.Contains("Granted: " + 2.25m.ToString("N2", System.Globalization.CultureInfo.CurrentCulture) + " USD", usd);
        Assert.DoesNotContain("USD", cny); Assert.Contains("Paid:", cny); Assert.Contains("Granted:", cny);
    }
    [Fact]
    public void MissingApiBalanceComponentsAreNotInvented()
    {
        using var document = JsonDocument.Parse("""{"balance_infos":[{"currency":"USD","total_balance":"-1.25","granted_balance":"0"}]}""");
        var reading = DeepSeekBalance.Parse(document.RootElement, "api");
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.StartsWith((-1.25m).ToString("N2", System.Globalization.CultureInfo.CurrentCulture), reading.Headline!.DisplayValue);
        Assert.DoesNotContain("Paid:", reading.Headline.DisplayValue); Assert.Contains("Granted:", reading.Headline.DisplayValue);
    }
    [Theory]
    [InlineData("1e-100")]
    [InlineData("-1e-100")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("-1.00000000000000000000000000001")]
    public void BalanceCannotRoundUnsupportedPrecisionIntoZeroOrAnotherAmount(string value)
    {
        using var document = JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { balance_infos = new[] { new { currency = "USD", total_balance = value } } }));
        Assert.Equal(ReadingState.Error, DeepSeekBalance.Parse(document.RootElement, "api").State);
    }
    [Fact]
    public void PositiveCnyIsNotHiddenBehindAnEmptyUsdWallet()
    {
        using var document = JsonDocument.Parse("""{"balance_infos":[{"currency":"USD","total_balance":"0"},{"currency":"CNY","total_balance":"10"}]}""");
        Assert.Equal("CNY", DeepSeekBalance.Parse(document.RootElement, "api").Headline!.Unit);
    }
    [Theory]
    [InlineData("""{"balance_infos":[]}""", "api")]
    [InlineData("""{"data":{"biz_data":{"normal_wallets":[],"bonus_wallets":[]}}}""", "web")]
    public void AuthoritativeEmptyWalletsClearOldBalance(string body, string source)
    {
        using var document = JsonDocument.Parse(body);
        var reading = DeepSeekBalance.Parse(document.RootElement, source);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Empty(reading.Windows);
        Assert.Empty(CodeRim.Core.Services.ReadingRetention.Merge(reading,
            new("deepseek", ReadingState.Ready, [new("USD", "Old balance", Unit: "USD", DisplayValue: "10 USD")])).Windows);
    }
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ReadingState.NeedsAuth)]
    [InlineData(HttpStatusCode.TooManyRequests, ReadingState.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ReadingState.Error)]
    [InlineData(HttpStatusCode.Found, ReadingState.Error)]
    public async Task PlatformTransportFailuresRemainDistinct(HttpStatusCode status, ReadingState expected)
    {
        using var provider = new HttpProviders(new Handler(_ => new(status) { Content = new StringContent("{}") }));
        var reading = await provider.FetchAsync("deepseek", "platform", _ => "web", TestContext.Current.CancellationToken);
        Assert.Equal(expected, reading.State); Assert.Empty(reading.Windows);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request));
    }
}
