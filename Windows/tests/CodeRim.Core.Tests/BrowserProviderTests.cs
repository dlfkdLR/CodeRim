using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;

public sealed class BrowserProviderTests
{
    private static ProviderReading Parse(string id, string json, params (string Key, string Json)[] extra)
    {
        JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
        var data = new Dictionary<string, JsonElement> { ["main"] = Json(json) };
        foreach (var item in extra) data[item.Key] = Json(item.Json);
        return NativeProviders.Parse(id, data);
    }
    [Fact]
    public void MiMoPreservesCurrencyAndTokenPool()
    {
        var reading = Parse("mimo", """{"code":0,"data":{"balance":"50.5","cashBalance":"40","giftBalance":"10.5","currency":"CNY"}}""",
            ("detail", """{"code":0,"data":{"planCode":"Pro","currentPeriodEnd":"2026-10-01 00:00:00"}}"""),
            ("usage", """{"code":0,"data":{"monthUsage":{"items":[{"name":"Monthly tokens","used":25,"limit":100,"percent":25}]}}}"""));
        Assert.Equal("tokens", reading.Headline!.Unit); Assert.Equal(25, reading.Headline.UsedPercent);
        Assert.Equal(TimeSpan.Zero, reading.Headline.ResetsAt!.Value.Offset); Assert.Equal("CNY", reading.Windows[1].Unit); Assert.Null(reading.Windows[1].UsedPercent);
    }
    [Fact]
    public void AbacusOptionalBillingCannotFabricateReset()
    {
        var reading = Parse("abacus", """{"success":true,"result":{"totalComputePoints":1000,"computePointsLeft":750}}""");
        Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal("credits", reading.Headline.Unit); Assert.Null(reading.Headline.ResetsAt);
        Assert.Equal(ReadingState.NeedsAuth, Parse("abacus", """{"success":false,"error":"Session expired"}""").State);
    }
    [Fact]
    public void StepFunCreditBucketsUseWeightedBalanceAndNoFakeRollingWindow()
    {
        var reading = Parse("stepfun", """{"status":1,"five_hour_usage_left_rate":0,"weekly_usage_left_rate":0,"five_hour_usage_reset_time":"0","weekly_usage_reset_time":"0","plan_credit_rate_limit":{"subscription_credit_left_rate":0.2,"topup_credit_left_rate":1,"subscription_credit_reset_time":"1790812800","credit_buckets":[{"credit_total":"100","credit_residual":"20"},{"credit_total":900,"credit_residual":900}]}}""");
        Assert.Single(reading.Windows); Assert.Equal("credits", reading.Headline!.Id); Assert.Equal(8, reading.Headline.UsedPercent!.Value, 5);
    }
    [Fact]
    public void StepFunLiveWindowsOverrideFamilyAndDoNotSumIndependentFractions()
    {
        var windows = Parse("stepfun", """{"status":1,"plan_family":2,"five_hour_usage_left_rate":"0.75","weekly_usage_left_rate":0.5,"five_hour_usage_reset_time":"1790812800","weekly_usage_reset_time":"1790812800","plan_credit_rate_limit":{"subscription_credit_left_rate":0.1}}""");
        Assert.Equal(2, windows.Windows.Count); Assert.Equal(25, windows.Headline!.UsedPercent); Assert.Equal(50, windows.Windows[1].UsedPercent);
        var credits = Parse("stepfun", """{"status":1,"plan_credit_rate_limit":{"subscription_credit_left_rate":0.2,"topup_credit_left_rate":1}}""");
        Assert.Equal(80, credits.Headline!.UsedPercent);
    }
    [Fact]
    public void SakanaParsesWindowBoundariesAndUtcServerDate()
    {
        var html = "<p>5-hour</p><p>25% used</p><p>Resets on October 1, 2026 at 1:00 PM</p><p>Weekly</p><p>60% used</p>";
        var reading = Parse("sakana", JsonSerializer.Serialize(new { html }));
        Assert.Equal(2, reading.Windows.Count); Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal(60, reading.Windows[1].UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero), reading.Headline.ResetsAt); Assert.Null(reading.Windows[1].ResetsAt);
    }
    [Fact]
    public async Task BrowserAuthUsesCookieAndOptionalFailuresPreservePrimary()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Null(request.Headers.Authorization); Assert.Contains("api-platform_serviceToken=fixture", request.Headers.GetValues("Cookie").Single());
            Assert.DoesNotContain("unrelated", request.Headers.GetValues("Cookie").Single());
            await Task.Yield();
            return request.RequestUri!.AbsolutePath.EndsWith("/balance", StringComparison.Ordinal)
                ? new(HttpStatusCode.OK) { Content = new StringContent("""{"code":0,"data":{"balance":"50","currency":"CNY"}}""") }
                : new(HttpStatusCode.InternalServerError);
        });
        using var provider = new NativeProviders(handler);
        var result = await provider.FetchAsync("mimo", "userId=fixture; api-platform_serviceToken=fixture; unrelated=fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Partial, result.State); Assert.Single(result.Windows); Assert.Equal("CNY", result.Headline!.Unit);
    }
    [Fact]
    public async Task StepFunDeviceComesFromRefreshHalf()
    {
        var jwt = "e30." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("""{"device_id":"fixture-device"}""")).TrimEnd('=').Replace('+','-').Replace('/','_') + ".fixture";
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("fixture-device", request.Headers.GetValues("oasis-webid").Single());
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"status":1,"plan_family":2,"plan_credit_rate_limit":{"subscription_credit_left_rate":0.25}}""") });
        });
        using var provider = new NativeProviders(handler);
        Assert.Equal(75, (await provider.FetchAsync("stepfun", "Oasis-Token=access..." + jwt + "; Other=fixture", _ => null, TestContext.Current.CancellationToken)).Headline!.UsedPercent);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
    }
}
