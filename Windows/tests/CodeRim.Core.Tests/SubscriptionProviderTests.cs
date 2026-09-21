using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;

public sealed class SubscriptionProviderTests
{
    private static ProviderReading Parse(string id, string value)
    { using var doc = JsonDocument.Parse(value); return NativeProviders.Parse(id, new Dictionary<string, JsonElement> { ["main"] = doc.RootElement.Clone() }); }
    [Fact]
    public void KiloMicroDollarsAndPassBonusAreIndependent()
    {
        var result = Parse("kilo", """[{"result":{"data":{"json":{"creditBlocks":[{"amount_mUsd":10000000,"balance_mUsd":4000000}]}}}},{"result":{"data":{"json":{"subscription":{"tier":"tier_19","currentPeriodUsageUsd":5,"currentPeriodBaseCreditsUsd":10,"currentPeriodBonusCreditsUsd":10,"nextBillingAt":"2026-10-01T00:00:00Z"}}}}},{"error":{"json":{"message":"Optional payment unavailable"}}}]""");
        Assert.Equal(25, result.Headline!.UsedPercent); Assert.Equal(60, result.Windows[1].UsedPercent); Assert.Equal("USD", result.Windows[1].Unit);
        Assert.StartsWith("4", result.Windows[1].DisplayValue);
    }
    [Fact]
    public void KiloBalanceWithoutDenominatorIsMoney()
    {
        var result = Parse("kilo", """{"0":{"result":{"data":{"json":{"totalBalance_mUsd":5000000}}}},"1":{"result":{"data":{"json":null}}}}""");
        Assert.Single(result.Windows); Assert.Null(result.Headline!.UsedPercent); Assert.StartsWith("5", result.Headline.DisplayValue);
    }
    [Fact]
    public void DevinHideDailyAndOnePercentAreRespected()
    {
        var result = Parse("devin", """{"hide_daily_quota":true,"daily_percentage":0.5,"weekly_percentage":1,"weekly_reset_at":"1790812800000","overage_balance_cents":1250}""");
        Assert.Equal(2, result.Windows.Count); Assert.Equal("weekly", result.Headline!.Id); Assert.Equal(1, result.Headline.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), result.Headline.ResetsAt);
        Assert.StartsWith("12", result.Windows[1].DisplayValue); Assert.Null(result.Windows[1].UsedPercent);
    }
    [Theory]
    [InlineData("general")]
    [InlineData("General")]
    [InlineData("MiniMax-M2")]
    public void MiniMaxTextIntervalAndWeeklyPrecedeVideoRegardlessOfArrayOrder(string textModel)
    {
        var value = JsonSerializer.Serialize(new { data = new { model_remains = new[] {
            new { model_name = "video", current_interval_remaining_percent = 30, current_weekly_remaining_percent = (int?)null },
            new { model_name = textModel, current_interval_remaining_percent = 96, current_weekly_remaining_percent = (int?)99 }
        }}});
        var result = Parse("minimax", value);
        Assert.Equal(4, result.Headline!.UsedPercent); Assert.Equal(1, result.Windows[1].UsedPercent);
        Assert.Equal(70, result.Windows[2].UsedPercent);
    }
    [Theory]
    [InlineData("text-generation", "Weekly")]
    [InlineData("Text_Generation", "Weekly")]
    [InlineData("TextGeneration", " Weekly ")]
    public void MiniMaxServiceFallbackUsesTheSameTextThenWeeklyOrdering(string service, string window)
    {
        var result = Parse("minimax", JsonSerializer.Serialize(new { services = new[] {
            new { service_type = "video", window_type = "Today", limit = 100, usage = 70 },
            new { service_type = service, window_type = window, limit = 100, usage = 1 },
            new { service_type = service, window_type = "5h", limit = 100, usage = 4 } } }));
        Assert.Equal(4, result.Headline!.UsedPercent); Assert.Equal(1, result.Windows[1].UsedPercent); Assert.Equal(70, result.Windows[2].UsedPercent);
    }
    [Fact]
    public void MiniMaxRemainingCountersAreNotUsedCounters()
    {
        var result = Parse("minimax", """{"base_resp":{"status_code":0},"model_remains":[{"model_name":"MiniMax-M2","current_interval_total_count":100,"current_interval_usage_count":75,"current_weekly_total_count":1000,"current_weekly_usage_count":600}]}""");
        Assert.Equal(25, result.Headline!.UsedPercent); Assert.Equal(40, result.Windows[1].UsedPercent);
    }
    [Fact]
    public void MiniMaxPercentageOnlyUnavailableAndUnlimitedAreDistinct()
    {
        var result = Parse("minimax", """{"data":{"model_remains":[{"model_name":"general","current_interval_total_count":0,"current_interval_usage_count":0,"current_interval_remaining_percent":75,"current_weekly_status":3,"current_weekly_remaining_percent":100},{"model_name":"video","current_interval_total_count":0,"current_interval_usage_count":0,"current_interval_remaining_percent":100,"current_interval_status":3}]}}""");
        Assert.Equal(2, result.Windows.Count); Assert.Equal(25, result.Headline!.UsedPercent); Assert.Equal("Unlimited", result.Windows[1].DisplayValue); Assert.Null(result.Windows[1].UsedPercent);
    }
    [Fact]
    public async Task DevinUsesOrganizationHeaderAndBoundedFallbackPaths()
    {
        var paths = new List<string>();
        using var handler = new Handler(request =>
        {
            Assert.Equal("Bearer fixture", request.Headers.Authorization!.ToString()); Assert.Equal("org-fixture", request.Headers.GetValues("x-cog-org-id").Single());
            paths.Add(request.RequestUri!.AbsolutePath);
            return paths.Count == 1 ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = new StringContent("""{"weekly_percentage":0.25}""") };
        });
        using var client = new NativeProviders(handler);
        var result = await client.FetchAsync("devin", "Authorization: Bearer fixture", _ => "org-fixture", TestContext.Current.CancellationToken);
        Assert.Equal(25, result.Headline!.UsedPercent); Assert.Equal(2, paths.Count); Assert.Equal("/api/org-fixture/billing/quota/usage", paths[0]); Assert.Equal("/api/organizations/org-fixture/billing/quota/usage", paths[1]);
    }
    [Fact]
    public async Task MiniMaxRegionAndLegacyFallbackUseReadOnlyEndpoints()
    {
        var paths = new List<string>();
        using var handler = new Handler(request =>
        {
            Assert.Equal("api.minimaxi.com", request.RequestUri!.Host); Assert.Equal(HttpMethod.Get, request.Method); paths.Add(request.RequestUri.AbsolutePath);
            return paths.Count == 1 ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = new StringContent("""{"model_remains":[{"model_name":"general","current_interval_remaining_percent":75}]}""") };
        });
        using var client = new NativeProviders(handler);
        var result = await client.FetchAsync("minimax", "fixture", _ => "cn", TestContext.Current.CancellationToken);
        Assert.Equal(25, result.Headline!.UsedPercent); Assert.Equal(2, paths.Count);
        Assert.DoesNotContain("MINIMAX_COOKIE", NativeProviders.CredentialKeys("minimax")!);
    }

    [Fact]
    public void KiloLegacyPassPreservesMicroDollarsBonusAndRenewal()
    {
        var reading = Parse("kilo", """[{"result":{"data":{"json":{"blocks":[{"usedCredits":0,"totalCredits":19,"remainingCredits":19}]}}}},{"result":{"data":{"json":{"planName":"Starter","amount_mUsd":28500000,"used_mUsd":3500000,"bonus_mUsd":9500000,"nextRenewalAt":"2026-10-01T00:00:00Z"}}}},{"result":{"data":{"json":{"enabled":false}}}}]""");
        Assert.Equal("pass", reading.Headline!.Id); Assert.Equal(3.5 / 28.5 * 100, reading.Headline.UsedPercent);
        Assert.NotNull(reading.Headline.ResetsAt); Assert.Equal("bonus", reading.Windows[^1].Id); Assert.StartsWith("9", reading.Windows[^1].DisplayValue);
    }
    [Theory]
    [InlineData("405")]
    [InlineData("malformed")]
    [InlineData("network")]
    public async Task MiniMaxUnavailableModernApiFallsBack(string failure)
    {
        var calls = 0;
        using var handler = new Handler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                if (failure == "network") throw new HttpRequestException("Fixture");
                return new(failure == "405" ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.OK) { Content = new StringContent("malformed") };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"model_remains":[{"model_name":"general","current_interval_remaining_percent":75}]}""") };
        });
        using var provider = new NativeProviders(handler);
        Assert.Equal(25, (await provider.FetchAsync("minimax", "fixture", _ => "cn", TestContext.Current.CancellationToken)).Headline!.UsedPercent);
        Assert.Equal(2, calls);
    }
    [Fact]
    public async Task MiniMaxKeepsCredentialRejectionWhenLegacyIsMissing()
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(_ => ++calls == 1
            ? new(HttpStatusCode.OK) { Content = new StringContent("""{"base_resp":{"status_code":1004,"status_msg":"invalid api key"}}""") }
            : new(HttpStatusCode.NotFound)));
        Assert.Equal(ReadingState.NeedsAuth, (await provider.FetchAsync("minimax", "fixture", _ => "cn", TestContext.Current.CancellationToken)).State);
    }
    [Fact]
    public async Task KiloStructuredAuthenticationCodeWinsOverGenericMessage()
    {
        using var provider = new NativeProviders(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("""[{"error":{"json":{"message":"Missing bearer token","data":{"code":"UNAUTHORIZED"}}}}]""") }));
        Assert.Equal(ReadingState.NeedsAuth, (await provider.FetchAsync("kilo", "fixture", _ => null, TestContext.Current.CancellationToken)).State);
    }

    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request)); }
}
