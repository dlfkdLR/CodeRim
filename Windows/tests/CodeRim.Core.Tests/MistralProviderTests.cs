using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;
public sealed class MistralProviderTests
{
    private static ProviderReading Parse(string main, string credits = "{}", string vibe = "[]")
    {
        JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
        return NativeProviders.Parse("mistral", new Dictionary<string, JsonElement> { ["main"] = Json(main), ["credits"] = Json(credits), ["vibe"] = Json(vibe) });
    }
    [Fact]
    public void MistralUsesPaidUnitsAndKeepsCurrencyAndNonTokenBilling()
    {
        var reading = Parse("""
            {"currency":"eur","start_date":"2026-09-01","end_date":"2026-09-30",
             "prices":[{"billing_metric":"token","billing_group":"a","price":"0.5"},{"billing_metric":"page","billing_group":"b","price":"2"}],
             "completion":{"models":{"model":{"input":[{"value":100,"value_paid":10,"billing_metric":"token","billing_group":"a","timestamp":"2026-09-01"}],"cached":[{"value":5,"billing_metric":"token","billing_group":"a","timestamp":"2026-09-01"}]}}},
             "ocr":{"models":{"ocr":{"output":[{"value":3,"billing_metric":"page","billing_group":"b","timestamp":"2026-09-01"}]}}}}
            """, """{"wallet_amount":100,"credit_notes_amount":20,"ongoing_usage_balance":30,"currency":"eur"}""",
            """[{"result":{"data":{"json":{"usagePercentage":25,"resetAt":"2026-10-01T00:00:00Z"}}}}]""");
        Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal("EUR", reading.Windows.Single(x => x.Id == "spend").Unit);
        Assert.StartsWith("13", reading.Windows.Single(x => x.Id == "spend").DisplayValue);
        Assert.StartsWith("90", reading.Windows.Single(x => x.Id == "balance").DisplayValue);
        Assert.Equal(10, reading.Windows.Single(x => x.Id == "input").UsedCount);
        Assert.Equal(5, reading.Windows.Single(x => x.Id == "cached").UsedCount);
        Assert.Equal(0, reading.Windows.Single(x => x.Id == "output").UsedCount);
        Assert.Equal("EUR", reading.CostUsage!.Currency); Assert.Equal(15, reading.CostUsage.Entries.Sum(x => x.InputTokens ?? 0));
    }
    [Fact]
    public void MissingPricesDoNotBecomeFreeUsage()
    {
        var reading = Parse("""{"currency":"USD","completion":{"models":{"m":{"input":[{"value":100}]}}}}""");
        Assert.Equal(ReadingState.Partial, reading.State); Assert.DoesNotContain(reading.Windows, x => x.Id == "spend");
        Assert.Equal(100, reading.Headline!.UsedCount);
    }
    [Fact]
    public async Task ConsoleRequestReceivesOnlyItsSessionAndCsrfCookies()
    {
        var consoleCalls = 0;
        using var provider = new NativeProviders(new Handler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("csrf", request.Headers.GetValues("X-CSRFToken").Single());
            if (request.RequestUri!.Host == "console.mistral.ai")
            {
                consoleCalls++;
                Assert.Equal("csrftoken=csrf; ory_session_fixture=session", request.Headers.GetValues("Cookie").Single());
                return Ok("""[{"result":{"data":{"json":{"usagePercentage":25}}}}]""");
            }
            Assert.Contains("admin_only=private", request.Headers.GetValues("Cookie").Single());
            return request.RequestUri.AbsolutePath.EndsWith("credits", StringComparison.Ordinal)
                ? Ok("""{"wallet_amount":10,"currency":"USD"}""")
                : Ok("""{"currency":"USD","completion":{"models":{}}}""");
        }));
        var reading = await provider.FetchAsync("mistral", "csrftoken=csrf; admin_only=private; ory_session_fixture=session", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(1, consoleCalls); Assert.Equal(25, reading.Headline!.UsedPercent);
        Assert.DoesNotContain("MISTRAL_API_KEY", NativeProviders.CredentialKeys("mistral")!);
    }
    private static HttpResponseMessage Ok(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request)); }
}
