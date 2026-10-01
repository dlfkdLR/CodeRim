using System.Net;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class MobileRelayTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    public void SnapshotExcludesCredentialsPathsAndTitlesByDefault()
    {
        var now = DateTimeOffset.UtcNow;
        var local = new LocalUsage("this-pc", "ready", now, now, TimeZoneInfo.Local.Id, new() { ["today"] = new(10, 2, 5) });
        var source = new CompanionSnapshot(1, now, [new("codex", "Codex", true, local,
            new("codex", ReadingState.Ready, [new("weekly", "Weekly", 25, now.AddHours(1))], now, Message: "secret", Plan: "private account"), "private-account-id")]);
        var result = MobileSnapshotBuilder.Create(source, [new("private-session-path", "codex", "private title", "waiting", now)], false, now);
        var json = JsonSerializer.Serialize(result, Json);
        Assert.DoesNotContain("private", json, StringComparison.Ordinal); Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.Equal(15, result.Providers[0].TodayTokens); Assert.Equal(75, result.Providers[0].Windows[0].RemainingPercent);
        Assert.Equal("waiting", result.Sessions[0].Phase); Assert.Equal("", result.Sessions[0].Title);
        Assert.Equal("private title", MobileSnapshotBuilder.Create(source, [new("id", "codex", "private title", "waiting", now)], true, now).Sessions[0].Title);
    }
    [Theory]
    [InlineData("http://relay.example.com")]
    [InlineData("https://user:password@relay.example.com")]
    [InlineData("https://relay.example.com/path")]
    [InlineData("https://relay.example.com?token=x")]
    [InlineData("https://relay.example.com#fragment")]
    public void EndpointRejectsUnsafeOrigins(string address) => Assert.Throws<ArgumentException>(() => MobileRelayClient.ValidateEndpoint(address));

    [Fact]
    public async Task QrPairingIsWindowsScopedAndRedirectCannotSendBearerElsewhere()
    {
        var bearer = new string('A', 43);
        var offer = new MobilePairingStart(new string('i', 43), new string('s', 43), DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        var polls = 0;
        var handler = new FixtureHandler(async request => {
            var path = request.RequestUri!.AbsolutePath;
            if (path is "/v1/pairing/start" or "/v1/pairing/poll") Assert.Null(request.Headers.Authorization);
            if (path == "/v1/pairing/start")
            {
                Assert.Contains("windows", await request.Content!.ReadAsStringAsync(), StringComparison.Ordinal);
                return Json200(offer);
            }
            if (path == "/v1/pairing/poll")
                return ++polls == 1 ? Json200(new { pending = true, expiresAt = offer.ExpiresAt })
                    : Json200(new MobileToken(bearer, DateTimeOffset.UtcNow.AddDays(365).ToUnixTimeSeconds()));
            Assert.Equal(bearer, request.Headers.Authorization?.Parameter);
            var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://other.example.com"); return response;
        });
        using var client = new MobileRelayClient("https://relay.example.com", handler);
        var started = await client.StartPairingAsync("작업 PC", TestContext.Current.CancellationToken);
        Assert.Equal("coderim://pair?r=https%3A%2F%2Frelay.example.com&i=" + offer.Id + "&s=" + offer.Secret,
            MobilePairingLink.Create(client.Endpoint, started));
        Assert.Null(await client.PollPairingAsync(started, TestContext.Current.CancellationToken));
        var token = await client.PollPairingAsync(started, TestContext.Current.CancellationToken);
        Assert.Equal(bearer, token!.Token);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.DisconnectAsync(token.Token, TestContext.Current.CancellationToken));
        Assert.Equal(4, handler.Count);
    }
    [Fact]
    public void OnlyChangesAndHeartbeatsArePublished()
    {
        var now = DateTimeOffset.UtcNow;
        var body = new MobileSnapshot(1, 100, [], []);
        Assert.True(MobileSnapshotBuilder.ShouldSend(body, null, null, TimeSpan.FromMinutes(5), now));
        Assert.False(MobileSnapshotBuilder.ShouldSend(body with { GeneratedAt = 160 }, body, now, TimeSpan.FromMinutes(5), now.AddMinutes(1)));
        Assert.True(MobileSnapshotBuilder.ShouldSend(body with { GeneratedAt = 400 }, body, now, TimeSpan.FromMinutes(5), now.AddMinutes(5)));
        Assert.True(MobileSnapshotBuilder.ShouldSend(body with { Sessions = [new("codex", "working", "", null)] }, body, now, TimeSpan.FromMinutes(5), now.AddSeconds(20)));
    }
    private static HttpResponseMessage Json200(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json") };
    [Fact]
    public void RelayTokensRequireBoundedBase64UrlAndSaneFutureExpiry()
    {
        var now = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        var valid = new string('A', 43);
        Assert.Equal(valid, MobileRelayClient.ValidateIssuedToken(new(valid, now.AddDays(90).ToUnixTimeSeconds()), now).Token);
        Assert.Throws<InvalidDataException>(() => MobileRelayClient.ValidateIssuedToken(new(null!, now.AddDays(90).ToUnixTimeSeconds()), now));
        foreach (var token in new[] { "", new string('A', 31), new string('A', 129), new string('A', 42) + "+", new string('A', 42) + "\n" })
            Assert.Throws<InvalidDataException>(() => MobileRelayClient.ValidateIssuedToken(new(token, now.AddDays(90).ToUnixTimeSeconds()), now));
        foreach (var expiresAt in new[] { double.NaN, double.PositiveInfinity, now.ToUnixTimeSeconds(), now.AddDays(367).ToUnixTimeSeconds() })
            Assert.Throws<InvalidDataException>(() => MobileRelayClient.ValidateIssuedToken(new(valid, expiresAt), now));
    }
    [Fact]
    public void UnsupportedOrStaleSourcesAreNeverInventedAsLive()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new CompanionSnapshot(1, now, [new("codex", "Codex", true, null,
            new("codex", ReadingState.Ready, [new("weekly", "Weekly", 101)], now.AddHours(-1)))]);
        var result = MobileSnapshotBuilder.Create(source, [new("id", "codex", "", "unknown", now)], false, now);
        Assert.Equal("stale", result.Providers[0].State); Assert.Null(result.Providers[0].TodayTokens);
        Assert.Null(result.Providers[0].Windows[0].RemainingPercent); Assert.Equal("unavailable", result.Sessions[0].Phase);
    }
    private sealed class FixtureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Count++; return response(request); }
    }
}
