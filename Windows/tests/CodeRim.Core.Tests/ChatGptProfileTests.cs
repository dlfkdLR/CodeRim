using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class ChatGptProfileTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T00:30:00+09:00", CultureInfo.InvariantCulture);
    private static byte[] Payload() => Encoding.UTF8.GetBytes("""
        {"stats":{"lifetime_tokens":1000,"daily_usage_buckets":[
        {"start_date":"2026-08-31","tokens":100},{"start_date":"2026-09-20","tokens":20},
        {"start_date":"2026-09-21","tokens":30},{"start_date":"2026-09-22","tokens":40}]},
        "metadata":{"stats_as_of":"2026-09-22","generated_at":"2026-09-23T00:00:00Z","stats_error":null}}
        """);
    private static string Token(string subject) => "header." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { sub = subject })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
    [Fact]
    public void CalendarUsesLocalDateAndSelectedWeekStartWithoutAddingLocalUsage()
    {
        var monday = ChatGptProfileClient.Decode(Payload(), Now, WeekStart.Monday);
        var sunday = ChatGptProfileClient.Decode(Payload(), Now, WeekStart.Sunday);
        Assert.Equal(40, monday.Today); Assert.Equal(70, monday.Week); Assert.Equal(90, sunday.Week);
        Assert.Equal(90, monday.Month); Assert.Equal(1000, monday.Lifetime);
        Assert.Equal(new DateOnly(2026, 9, 22), monday.StatsAsOf);
        Assert.Equal(0, ChatGptProfileClient.Decode(Payload(), Now.AddMonths(1), WeekStart.Monday).Month);
    }
    [Theory]
    [InlineData("negative")][InlineData("decimal")][InlineData("duplicate")][InlineData("future")]
    [InlineData("invalid-date")][InlineData("non-padded-date")][InlineData("overflow")]
    [InlineData("server-error")][InlineData("missing-error")][InlineData("missing-stats")]
    [InlineData("invalid-generated")][InlineData("missing-zone")][InlineData("generated-whitespace")]
    public void InvalidResponsesDoNotInventTotals(string mutation)
    {
        var json = JsonNode.Parse(Payload())!; var buckets = json["stats"]!["daily_usage_buckets"]!.AsArray();
        switch (mutation)
        {
            case "negative": json["stats"]!["lifetime_tokens"] = -1; break;
            case "decimal": buckets[0]!["tokens"] = 1.1; break;
            case "duplicate": buckets.Add(buckets[0]!.DeepClone()); break;
            case "future": buckets[0]!["start_date"] = "2026-09-23"; break;
            case "invalid-date": buckets[0]!["start_date"] = "2026-02-30"; break;
            case "non-padded-date": buckets[0]!["start_date"] = "2026-9-01"; break;
            case "overflow": buckets[2]!["tokens"] = long.MaxValue; break;
            case "server-error": json["metadata"]!["stats_error"] = "unavailable"; break;
            case "missing-error": json["metadata"]!.AsObject().Remove("stats_error"); break;
            case "missing-stats": json.AsObject().Remove("stats"); break;
            case "invalid-generated": json["metadata"]!["generated_at"] = "tomorrow"; break;
            case "missing-zone": json["metadata"]!["generated_at"] = "2026-09-23T00:00:00"; break;
            case "generated-whitespace": json["metadata"]!["generated_at"] = " 2026-09-23T00:00:00Z"; break;
        }
        Assert.Throws<InvalidDataException>(() => ChatGptProfileClient.Decode(Encoding.UTF8.GetBytes(json.ToJsonString()), Now, WeekStart.Monday));
    }
    [Theory]
    [InlineData(401)][InlineData(403)][InlineData(302)][InlineData(500)]
    public async Task RejectedResponsesNeverReturnHistory(int status)
    {
        using var client = new ChatGptProfileClient(() => new(Token("a"), "workspace"), new Handler((request, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { RequestMessage = request })));
        var error = await Record.ExceptionAsync(() => client.FetchAsync(Now, WeekStart.Monday, TestContext.Current.CancellationToken));
        Assert.NotNull(error); Assert.True(error is ProfileCredentialException or InvalidDataException);
    }
    [Fact]
    public async Task RequestIsBoundedFixedReadOnlyAndReturnsOnlyTheCurrentAccount()
    {
        var credential = new ProfileCredential(Token("a"), "workspace");
        using var client = new ChatGptProfileClient(() => credential, new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal(ChatGptProfileClient.Endpoint, request.RequestUri);
            Assert.True(request.Headers.CacheControl!.NoStore); Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("workspace", request.Headers.GetValues("ChatGPT-Account-ID").Single());
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(Payload()) });
        }));
        var result = await client.FetchAsync(Now, WeekStart.Monday, TestContext.Current.CancellationToken);
        Assert.Equal(credential.AccountKey, result.AccountKey); Assert.Equal(1000, result.Lifetime);
        Assert.DoesNotContain(credential.AccessToken, credential.ToString());
    }
    [Fact]
    public async Task AccountSwitchDuringRequestRejectsResponseEvenInsideSameWorkspace()
    {
        var credential = new ProfileCredential(Token("a"), "workspace");
        using var client = new ChatGptProfileClient(() => credential, new Handler((request, _) =>
        {
            credential = new(Token("b"), "workspace");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(Payload()) });
        }));
        await Assert.ThrowsAsync<ProfileCredentialException>(() => client.FetchAsync(Now, WeekStart.Monday, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task OversizedBodyAndWrongFinalUrlAreRejected()
    {
        using var large = new ChatGptProfileClient(() => new(Token("a"), "workspace"), new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(new byte[ChatGptProfileClient.MaximumResponseBytes + 1]) })));
        await Assert.ThrowsAsync<InvalidDataException>(() => large.FetchAsync(Now, WeekStart.Monday, TestContext.Current.CancellationToken));
        using var wrong = new ChatGptProfileClient(() => new(Token("a"), "workspace"), new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new(HttpMethod.Get, "https://example.invalid"), Content = new ByteArrayContent(Payload()) })));
        await Assert.ThrowsAsync<InvalidDataException>(() => wrong.FetchAsync(Now, WeekStart.Monday, TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData("")][InlineData(" ")][InlineData("secret\r\nheader:value")][InlineData("secret\u0000")]
    public void InvalidHeadersAreRejectedWithoutLeakingValues(string value)
    {
        Assert.Throws<ProfileCredentialException>(() => new ProfileCredential(value, "account"));
        Assert.Throws<ProfileCredentialException>(() => new ProfileCredential("token", value));
    }
    [Fact]
    public void AccessTokenSubjectAndWorkspaceBothIdentifyOwnership()
    {
        var first = new ProfileCredential(Token("first"), "shared");
        Assert.NotEqual(first.AccountKey, new ProfileCredential(Token("second"), "shared").AccountKey);
        Assert.NotEqual(first.AccountKey, new ProfileCredential(Token("first"), "other").AccountKey);
        Assert.NotNull(ProfileCredential.Parse(JsonSerializer.Serialize(new { tokens = new { access_token = Token("first"), account_id = "shared" } })).AccountKey);
        Assert.Null(new ProfileCredential("opaque", "shared").AccountKey);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
