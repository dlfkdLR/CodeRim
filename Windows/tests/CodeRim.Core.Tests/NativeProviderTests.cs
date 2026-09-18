using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;

public sealed class NativeProviderTests
{
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static ProviderReading Parse(string id, string json, params (string Key, string Value)[] extra)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["main"] = Json(json) };
        foreach (var item in extra) payload[item.Key] = Json(item.Value);
        return NativeProviders.Parse(id, payload);
    }
    [Fact]
    public void OpenCodeKeepsZeroAndAccountWindows()
    {
        var reading = Parse("opencode", """{"usage":{"rolling":{"percent":0,"resetsAt":"2026-10-01T12:00:00.123Z"},"weekly":{"percent":42},"monthly":{"percent":16}}}""");
        Assert.Equal(3, reading.Windows.Count); Assert.Equal(0, reading.Headline!.UsedPercent); Assert.Equal(300, reading.Headline.DurationMinutes);
    }
    [Fact]
    public void OllamaFractionsAreNotMoneyAndDoNotInventReset()
    {
        var reading = Parse("ollama", """{"limits":{"monthly":{"usage":0.152,"models":[{"name":"test","request_count":8}]}}}""");
        Assert.Equal(15.2, reading.Headline!.UsedPercent!.Value, 5); Assert.Null(reading.Headline.ResetsAt);
        Assert.Equal("requests", reading.Windows[1].Unit); Assert.Equal(8, reading.Windows[1].UsedCount);
    }
    [Fact]
    public void CursorUsesAutoInsteadOfBlendedTotal()
    {
        var reading = Parse("cursor", """{"individualUsage":{"plan":{"autoPercentUsed":0,"apiPercentUsed":80,"totalPercentUsed":60}}}""");
        Assert.Equal("auto", reading.Headline!.Id); Assert.Equal(0, reading.Headline.UsedPercent); Assert.Equal(80, reading.Windows[1].UsedPercent);
    }
    [Fact]
    public void GrokFreshWeeklyPlanHasZeroReading()
    {
        var reading = Parse("grok", """{"config":{"currentPeriod":{"type":"WEEKLY","end":"2026-10-01T12:00:00Z"}}}""");
        Assert.Equal(0, reading.Headline!.UsedPercent); Assert.Equal("Weekly limit", reading.Headline.Name);
    }
    [Fact]
    public void CommandCodeUsesMonthlyBalanceAndPreservesAbsentReset()
    {
        var reading = Parse("commandcode", "{}", ("usage", """{"totalCost":20}"""), ("credits", """{"credits":{"monthlyCredits":80},"windowLimits":{"fiveHour":{"cap":40,"used":8,"resetAt":0}}}"""));
        Assert.Equal(20, reading.Headline!.UsedPercent); Assert.Null(reading.Windows[1].ResetsAt);
    }
    [Fact]
    public void FireworksDoesNotMixCurrenciesOrInventQuota()
    {
        var reading = Parse("fireworks", """{"lineItems":[{"totalCost":{"units":"2","nanos":500000000,"currencyCode":"USD"}},{"totalCost":{"units":"200","nanos":0,"currencyCode":"EUR"}}]}""");
        Assert.Null(reading.Headline!.UsedPercent); Assert.Equal("USD", reading.Headline.Unit); Assert.StartsWith("2", reading.Headline.DisplayValue);
    }
    [Fact]
    public void DeepInfraConvertsOnlyUsageCents()
    {
        var reading = Parse("deepinfra", """{"stripe_balance":-100,"recent":20}""", ("usage", """{"months":[{"total_cost":1234}]}"""));
        Assert.StartsWith("80", reading.Windows[0].DisplayValue); Assert.StartsWith("12", reading.Windows[2].DisplayValue); Assert.All(reading.Windows, x => Assert.Null(x.UsedPercent));
    }
    [Fact]
    public void CodebuffBalanceWithoutQuotaIsCredits()
    {
        var reading = Parse("codebuff", """{"remainingBalance":1200}""");
        Assert.Equal("credits", reading.Headline!.Unit); Assert.Null(reading.Headline.UsedPercent);
    }
    [Fact]
    public void NeuralwattReportsAccountBalanceAndKeyAllowanceSeparately()
    {
        var reading = Parse("neuralwatt", """{"balance":{"credits_remaining_usd":75,"total_credits_usd":100},"key":{"allowance":{"limit_usd":10,"spent_usd":5}}}""");
        Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal(50, reading.Windows.Single(x => x.Id == "key").UsedPercent);
    }
    [Fact]
    public async Task GrokCredentialIsSentOnlyToFixedVendorEndpoint()
    {
        using var handler = new RecordingHandler(); using var provider = new NativeProviders(handler);
        var result = await provider.FetchAsync("grok", "fixture-token", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, result.State); Assert.Equal("cli-chat-proxy.grok.com", handler.Host);
        Assert.Equal("Bearer fixture-token", handler.Authorization); Assert.Equal("xai-grok-cli", handler.TokenHeader);
    }
    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal string? Host, Authorization, TokenHeader;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Host = request.RequestUri!.Host; Authorization = request.Headers.Authorization!.ToString(); TokenHeader = request.Headers.GetValues("X-XAI-Token-Auth").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"config":{"creditUsagePercent":40}}""") });
        }
    }
}
