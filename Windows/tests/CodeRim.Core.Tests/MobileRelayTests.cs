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
    public async Task PairingIsWindowsScopedAndRedirectCannotSendBearerElsewhere()
    {
        var handler = new FixtureHandler(async request => {
            if (request.RequestUri!.AbsolutePath == "/v1/pairing/claim") {
                var body = await request.Content!.ReadAsStringAsync(); Assert.Contains("windows", body, StringComparison.Ordinal);
                Assert.DoesNotContain("Authorization", request.Headers.ToString(), StringComparison.Ordinal);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"test-token\",\"expiresAt\":1790000000}", Encoding.UTF8, "application/json") };
            }
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://other.example.com"); return response;
        });
        using var client = new MobileRelayClient("https://relay.example.com", handler);
        var token = await client.PairAsync("ABCD-EFGH", "작업 PC", TestContext.Current.CancellationToken);
        Assert.Equal("test-token", token.Token);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.DisconnectAsync(token.Token, TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Count);
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
