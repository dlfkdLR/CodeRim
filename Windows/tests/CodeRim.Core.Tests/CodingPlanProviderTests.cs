using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;

public sealed class CodingPlanProviderTests
{
    private static ProviderReading Parse(string id, string text)
    {
        using var doc = JsonDocument.Parse(text);
        return NativeProviders.Parse(id, new Dictionary<string, JsonElement> { ["main"] = doc.RootElement.Clone() });
    }
    private static ProviderReading Amp(string text) => Parse("amp", JsonSerializer.Serialize(new { ok = true, result = new { displayText = text } }));
    [Fact]
    public void KimiModernPoolsWinOverLegacyAndPreserveZero()
    {
        var reading = Parse("kimi", """{"usages":{"limit_5h":{"used_ratio":0},"limit_7d":{"used_ratio":0.25},"limit_month_total":{"used_ratio":0.8}},"usage":{"limit":"100","used":"70"},"user":{"membership":{"level":"LEVEL_BASIC"}},"version":"GOODS_VERSION_V1"}""");
        Assert.Equal("Moderato", reading.Plan); Assert.Equal(3, reading.Windows.Count); Assert.Equal(0, reading.Headline!.UsedPercent);
        Assert.Equal(25, reading.Windows[1].UsedPercent); Assert.Equal(80, reading.Windows[2].UsedPercent);
    }
    [Fact]
    public void KimiUnknownCountersDoNotBecomeFreshZero()
    {
        var reading = Parse("kimi", """{"usage":{"limit":"100"},"limits":[{"window":{"duration":5,"timeUnit":"TIME_UNIT_HOUR"},"detail":{"limit":100,"remaining":25,"reset_at":"2026-10-01T00:00:00Z"}}]}""");
        Assert.Single(reading.Windows); Assert.Equal(75, reading.Headline!.UsedPercent); Assert.Equal(300, reading.Headline.DurationMinutes);
        Assert.NotNull(reading.Headline.ResetsAt);
    }
    [Theory]
    [InlineData("https://api.kimi.com", "/coding/v1/usages")]
    [InlineData("https://api.kimi.com/coding", "/coding/v1/usages")]
    [InlineData("https://api.kimi.com/coding/v1", "/coding/v1/usages")]
    public async Task KimiUsesCodeKeyEndpoint(string baseUrl, string path)
    {
        using var handler = new Handler("""{"usages":{"limit_5h":{"used_ratio":0.25}}}""");
        using var client = new NativeProviders(handler);
        Assert.Equal(ReadingState.Ready, (await client.FetchAsync("kimi", "fixture-code-key", _ => baseUrl, TestContext.Current.CancellationToken)).State);
        Assert.Equal(path, handler.Path); Assert.Equal("Bearer fixture-code-key", handler.Authorization);
    }
    [Fact]
    public void AmpFreeAndCreditsUseTheirOwnUnits()
    {
        var reading = Amp("Amp Free: $5 / $20 remaining (replenishes +$0.50/hour)\nIndividual credits: $25.50 remaining\nWorkspace Team: $100 remaining");
        Assert.Equal(75, reading.Headline!.UsedPercent); Assert.Equal(3, reading.Windows.Count);
        Assert.All(reading.Windows.Skip(1), window => { Assert.Null(window.UsedPercent); Assert.Equal("USD", window.Unit); });
    }
    [Fact]
    public void AmpTierUsesDollarsAndIndependentOrbHoursAndUtcPeriod()
    {
        var reading = Amp("Amp Pro Tier: agent usage $75 of $100 remaining (rounded 80%), orb usage 5h of 20h a1.small orb hours remaining, period 2026-09-01 to 2026-10-01, resets upon renewal in 12 days");
        Assert.Equal("Pro", reading.Plan); Assert.Equal(25, reading.Headline!.UsedPercent); Assert.Equal(75, reading.Windows[1].UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), reading.Headline.ResetsAt); Assert.Equal(43200, reading.Headline.DurationMinutes);
    }
    [Theory]
    [InlineData("Subscription Pro: 80% other usage and 30% orb usage remaining - resets upon renewal in 12 days")]
    [InlineData("Amp Pro Subscription: 80% other usage and 30% orb usage remaining - resets upon renewal in 1 month")]
    public void AmpLegacyIndependentPercentagesDoNotInventExactReset(string text)
    {
        var reading = Amp(text); Assert.Equal(20, reading.Headline!.UsedPercent); Assert.Equal(70, reading.Windows[1].UsedPercent);
        Assert.Null(reading.Headline.ResetsAt);
    }
    [Fact]
    public void AmpDailyFreeAndExpiredCredentialsAreDistinct()
    {
        Assert.Equal(25, Amp("\u001b[32m**Amp Free:** 75% remaining today (resets daily)\u001b[0m").Headline!.UsedPercent);
        Assert.Equal(ReadingState.NeedsAuth, Parse("amp", """{"ok":false,"error":{"code":"auth-required"}}""").State);
    }
    [Fact]
    public async Task AmpUsesPostBody()
    {
        using var handler = new Handler("""{"ok":true,"result":{"displayText":"Individual credits: $20 remaining"}}""");
        using var client = new NativeProviders(handler);
        Assert.Equal(ReadingState.Ready, (await client.FetchAsync("amp", "fixture", _ => null, TestContext.Current.CancellationToken)).State);
        Assert.Equal(HttpMethod.Post, handler.Method);
        using var body = JsonDocument.Parse(handler.Body!); Assert.Equal("userDisplayBalanceInfo", body.RootElement.GetProperty("method").GetString());
    }
    private sealed class Handler(string payload) : HttpMessageHandler
    {
        internal string? Path, Authorization, Body; internal HttpMethod? Method;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath; Authorization = request.Headers.Authorization?.ToString(); Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(payload) };
        }
    }
}
