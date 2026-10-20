using System.Globalization;
using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;

public sealed class DeepSeekNumericTests
{
    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("+0.000e-1024", "0")]
    [InlineData("000012.3400", "12.34")]
    [InlineData(" +12.3400e+2 ", "1234")]
    [InlineData("123400e-4", "12.34")]
    [InlineData("1E-28", "0.0000000000000000000000000001")]
    [InlineData("10000000000000000000000000000e-28", "1")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335", "-79228162514264337593543950335")]
    [InlineData("-0000.0012300e+3", "-1.23")]
    [InlineData("1e00000000000000000000000000000000000001", "10")]
    public void ExactRepresentableMoneyPreservesValue(string text, string expected)
    {
        var value = JsonSerializer.SerializeToElement(text);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), DeepSeekUsageDetails.Number(value, allowNegative: true));
    }
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData(".1")]
    [InlineData("1.")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("1e-")]
    [InlineData("1.0.0")]
    [InlineData("1e1e1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e100")]
    [InlineData("1e-100")]
    [InlineData("1e-29")]
    [InlineData("-1e-100")]
    [InlineData("1e1025")]
    [InlineData("0e-1025")]
    [InlineData("0e2147483648")]
    [InlineData("0e-2147483649")]
    [InlineData("0e999999999999999999999999999999999999999")]
    [InlineData("1_000")]
    [InlineData("1,000")]
    [InlineData("0x10")]
    [InlineData("1 2")]
    [InlineData("1\u0000")]
    [InlineData("١")]
    [InlineData("１")]
    [InlineData("--1")]
    [InlineData("1e+-1")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("0.99999999999999999999999999999999999")]
    [InlineData("12345678901234567890123456789.1")]
    public void MalformedOrRoundedAmountsUseTheDataErrorContract(string text)
        => Assert.Throws<InvalidDataException>(() => DeepSeekUsageDetails.Number(JsonSerializer.SerializeToElement(text), allowNegative: true));
    [Fact]
    public void LengthBoundAndNegativeCountPolicyRemainExact()
    {
        Assert.Equal(0, DeepSeekUsageDetails.Number(JsonSerializer.SerializeToElement(new string('0', 128))));
        Assert.Throws<InvalidDataException>(() => DeepSeekUsageDetails.Number(JsonSerializer.SerializeToElement(new string('0', 129))));
        Assert.Throws<InvalidDataException>(() => DeepSeekUsageDetails.Number(JsonSerializer.SerializeToElement("-1")));
        Assert.Equal(0, DeepSeekUsageDetails.Number(JsonSerializer.SerializeToElement("-0e1024")));
        Assert.Throws<InvalidDataException>(() => DeepSeekUsageDetails.Number(JsonSerializer.SerializeToElement(true)));
        Assert.Throws<InvalidDataException>(() => DeepSeekUsageDetails.Number(JsonSerializer.SerializeToElement(new { value = 1 })));
    }
    [Fact]
    public void StringAndJsonNumberKeepTheSamePrecisionPolicy()
    {
        using var json = JsonDocument.Parse("[1.2300e2,-0.0,1e-29,79228162514264337593543950336]");
        Assert.Equal(123, DeepSeekUsageDetails.Number(json.RootElement[0]));
        Assert.Equal(0, DeepSeekUsageDetails.Number(json.RootElement[1]));
        Assert.Throws<InvalidDataException>(() => DeepSeekUsageDetails.Number(json.RootElement[2]));
        Assert.Throws<InvalidDataException>(() => DeepSeekUsageDetails.Number(json.RootElement[3]));
    }
    private static string Balance(string value) => JsonSerializer.Serialize(new { data = new { biz_data = new {
        normal_wallets = new[] { new { currency = "USD", balance = value } }, bonus_wallets = System.Array.Empty<object>() } } });
    private sealed class Handler(Func<HttpRequestMessage, string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(request)) });
    }
    [Theory]
    [InlineData("NaN")]
    [InlineData("1e100")]
    [InlineData("1e-100")]
    public async Task PrimaryMalformedMoneyReturnsAnErrorThroughRealHttpTransport(string number)
    {
        using var reader = new HttpProviders(new Handler(_ => Balance(number)));
        var reading = await reader.FetchAsync("deepseek", "synthetic", key => key == "DEEPSEEK_USAGE_SOURCE" ? "web" : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Empty(reading.Windows);
    }
    [Fact]
    public async Task OptionalMalformedCountsPreserveCurrentBalanceThroughMonthlyFallback()
    {
        var calls = 0;
        using var reader = new HttpProviders(new Handler(request => {
            Interlocked.Increment(ref calls); var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("get_user_summary", StringComparison.Ordinal)) return Balance("12.34");
            var now = DateTimeOffset.Now;
            var rolling = path.Contains("by_api_key", StringComparison.Ordinal);
            if (path.EndsWith("/cost", StringComparison.Ordinal))
            {
                object costs = rolling ? new { data = System.Array.Empty<object>() } : System.Array.Empty<object>();
                return JsonSerializer.Serialize(new { code = 0, data = new { biz_code = 0, biz_data = costs } });
            }
            object data = rolling
                ? new { series = new[] { new { buckets = new[] { new { time = now.ToUnixTimeSeconds(), usage = new { RESPONSE_TOKEN = "1e100" } } } } } }
                : new { days = new[] { new { date = now.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), data = new[] {
                    new { model = "fixture", usage = new[] { new { type = "RESPONSE_TOKEN", amount = "1e100" } } } } } } };
            return JsonSerializer.Serialize(new { code = 0, data = new { biz_code = 0, biz_data = data } });
        }));
        var reading = await reader.FetchAsync("deepseek", "synthetic", key => key == "DEEPSEEK_USAGE_SOURCE" ? "web" : "true", TestContext.Current.CancellationToken);
        Assert.Equal(5, calls); Assert.Equal(ReadingState.Partial, reading.State); Assert.Single(reading.Windows);
        Assert.Contains(12.34m.ToString("N2", CultureInfo.CurrentCulture), reading.Windows[0].DisplayValue);
    }
    [Fact]
    public void ConcurrentBoundedExternalNumbersDoNotEscapeAsRuntimeParserExceptions()
    {
        Parallel.For(0, 4096, index => {
            var invalid = index % 2 == 0;
            using var json = JsonDocument.Parse(Balance(invalid ? "1e100" : "12.34"));
            Assert.Equal(invalid ? ReadingState.Error : ReadingState.Ready, DeepSeekBalance.Parse(json.RootElement, "web").State);
        });
    }
}
