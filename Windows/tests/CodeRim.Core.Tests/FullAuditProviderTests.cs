using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using Xunit;

namespace CodeRim.Core.Tests;

public sealed class FullAuditProviderTests
{
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(respond(request));
    }

    [Theory]
    [InlineData(401, "{}")]
    [InlineData(403, "")]
    [InlineData(401, "<html>sign in</html>")]
    public async Task AuthoritativeAuthFailuresAreClassifiedBeforeBodyParsing(int status, string body)
    {
        foreach (var id in ScriptProviders.Catalog.Keys)
        {
            using var providers = new ScriptProviders(new Handler(_ => Response(body, (HttpStatusCode)status)));
            var reading = await providers.FetchAsync(id, key => key switch {
                "SUB2API_BASE_URL" => "https://fixture.invalid", "XAI_TEAM_ID" => "fixture",
                _ => ScriptProviders.Catalog[id].Settings.Any(s => s.Key == key && s.Type == "secure") ? "fixture" : null
            }, "session_id=fixture;session=fixture", TestContext.Current.CancellationToken);
            Assert.True(reading.State == ReadingState.NeedsAuth, id + ": " + reading.State);
            Assert.Empty(reading.Windows);
        }
    }

    [Theory]
    [InlineData("{}", 503, ReadingState.Partial, "Unavailable")]
    [InlineData("not json", 200, ReadingState.Partial, "Unavailable")]
    [InlineData("{\"timeSeries\":[]}", 200, ReadingState.Ready, "$0.00")]
    [InlineData("{\"timeSeries\":[],\"limitReached\":true}", 200, ReadingState.Partial, "$0.00")]
    [InlineData("<html>unauthorized optional history</html>", 401, ReadingState.Partial, "Unavailable")]
    public async Task XaiDistinguishesUnavailableAndPartialHistoryFromZero(string history, int status, ReadingState state, string value)
    {
        using var providers = new ScriptProviders(new Handler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("balance", StringComparison.Ordinal)
                ? Response("{\"total\":{\"val\":\"-1000\"}}") : Response(history, (HttpStatusCode)status)));
        var reading = await providers.FetchAsync("xai", _ => "fixture", null, TestContext.Current.CancellationToken);
        Assert.Equal(state, reading.State);
        Assert.Equal(value, Assert.Single(reading.Windows, x => x.Name.StartsWith("Last 30 days", StringComparison.Ordinal)).DisplayValue);
        Assert.Contains(reading.Windows, x => x.Name == "Prepaid balance" && x.DisplayValue == "$10.00");
    }

    [Theory]
    [InlineData("cap", ReadingState.Partial)]
    [InlineData("failure", ReadingState.Partial)]
    [InlineData("repeat", ReadingState.Partial)]
    [InlineData("complete", ReadingState.Ready)]
    [InlineData("cutoff", ReadingState.Ready)]
    public async Task PoeLabelsBoundedOrFailedHistoryAsPartial(string scenario, ReadingState expected)
    {
        var pages = 0;
        using var providers = new ScriptProviders(new Handler(request => {
            if (request.RequestUri!.AbsolutePath.EndsWith("current_balance", StringComparison.Ordinal))
                return Response("{\"current_point_balance\":100}");
            pages++;
            if (scenario == "failure" && pages == 2) return Response("{}", HttpStatusCode.ServiceUnavailable);
            var date = DateTimeOffset.UtcNow.AddDays(scenario == "cutoff" ? -31 : 0).ToUnixTimeSeconds();
            return Response(JsonSerializer.Serialize(new {
                data = new[] { new { query_id = scenario == "repeat" ? "same-request" : "page-" + pages, creation_time = date, cost_points = 10 } },
                has_more = scenario != "complete", next_cursor = scenario == "complete" ? null : scenario == "repeat" ? "same" : "page-" + pages
            }));
        }));
        var reading = await providers.FetchAsync("poe", _ => "fixture", null, TestContext.Current.CancellationToken);
        Assert.Equal(expected, reading.State);
        Assert.InRange(pages, 1, 5);
        if (scenario == "cap") Assert.Equal(5, pages);
        if (scenario == "repeat")
        {
            Assert.Equal(2, pages);
            Assert.Equal("10 points · 1 requests", Assert.Single(reading.Windows,
                x => x.Name.StartsWith("Last 30 days", StringComparison.Ordinal)).DisplayValue);
        }
        if (scenario != "cutoff")
            Assert.Equal(expected == ReadingState.Partial, Assert.Single(reading.Windows, x => x.Name.StartsWith("Last 30 days", StringComparison.Ordinal)).Name.Contains("(partial)", StringComparison.Ordinal));
    }
}
