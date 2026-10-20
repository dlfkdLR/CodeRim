using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using Xunit;

namespace CodeRim.Core.Tests;

public sealed class ScriptAuthBoundaryReviewTests
{
    private const string PrivateText = "REVIEWER_PRIVATE_RESPONSE_SENTINEL";
    private static string? Setting(string id, string key) => key switch
    {
        "SUB2API_BASE_URL" => "https://fixture.invalid",
        "XAI_TEAM_ID" => "fixture",
        _ => ScriptProviders.Catalog[id].Settings.Any(x => x.Key == key && x.Type == "secure") ? "fixture-secret" : null
    };
    private static HttpResponseMessage Response(string body, int status) => new((HttpStatusCode)status) { Content = new StringContent(body) };
    public static IEnumerable<object[]> AuthFailures()
    {
        foreach (var id in ScriptProviders.Catalog.Keys)
        foreach (var status in new[] {401, 403})
        foreach (var body in new[] {"{}", "", "<html>" + PrivateText + "</html>"})
            yield return new object[] {id, status, body};
    }
    [Theory]
    [MemberData(nameof(AuthFailures))]
    public async Task RequiredHttpAuthFailureHasTypedStateRegardlessOfBody(string id, int status, string body)
    {
        using var providers = new ScriptProviders(new Handler(_ => Response(body, status)));
        var result = await providers.FetchAsync(id, key => Setting(id, key), "session_id=fixture; session=fixture", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, result.State);
        Assert.Empty(result.Windows);
        Assert.DoesNotContain(PrivateText, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("credits", 401)] [InlineData("credits", 403)]
    [InlineData("key", 401)] [InlineData("key", 403)]
    [InlineData("activity", 401)] [InlineData("activity", 403)]
    public async Task OptionalAuthFailureKeepsUsableOpenRouterData(string failingEndpoint, int status)
    {
        using var providers = new ScriptProviders(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/" + failingEndpoint, StringComparison.Ordinal)) return Response("<html>" + PrivateText + "</html>", status);
            if (path.EndsWith("/credits", StringComparison.Ordinal)) return Response("{\"data\":{\"total_credits\":100,\"total_usage\":20}}", 200);
            if (path.EndsWith("/key", StringComparison.Ordinal)) return Response("{\"data\":{\"usage\":1,\"limit\":10,\"limit_remaining\":9}}", 200);
            return Response("{\"data\":[]}", 200);
        }));
        var result = await providers.FetchAsync("openrouter", key => Setting("openrouter", key), null, TestContext.Current.CancellationToken);
        Assert.Contains(result.State, new[] {ReadingState.Ready, ReadingState.Partial});
        Assert.NotEmpty(result.Windows);
        Assert.DoesNotContain(PrivateText, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        if (failingEndpoint != "key") Assert.Contains(result.Windows, window => window.UsedPercent == 10);
        else Assert.Contains(result.Windows, window => window.DisplayValue?.Contains("80.00", StringComparison.Ordinal) == true);
    }
    [Theory]
    [InlineData("synthetic")] [InlineData("poe")] [InlineData("openrouter")]
    public async Task TransientStatusWithAuthWordsIsNotAnAuthFailure(string id)
    {
        using var providers = new ScriptProviders(new Handler(_ => Response("authentication-expired missing-credential " + PrivateText, 503)));
        var result = await providers.FetchAsync(id, key => Setting(id, key), null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, result.State);
        Assert.DoesNotContain(PrivateText, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }
    [Fact]
    public async Task VendorMessageContainingAuthMarkerIsNotTrustedClassification()
    {
        using var providers = new ScriptProviders(new Handler(_ => Response("{\"success\":false,\"code\":500,\"msg\":\"authentication-expired missing-credential " + PrivateText + "\"}", 200)));
        var result = await providers.FetchAsync("glm", key => Setting("glm", key), null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, result.State);
        Assert.DoesNotContain(PrivateText, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }
    [Theory]
    [InlineData(503, 401, ReadingState.Error)]
    [InlineData(401, 503, ReadingState.NeedsAuth)]
    public async Task OptionalManagementCredentialDoesNotClassifyThePrimaryAccountKey(int primaryStatus, int managementStatus, ReadingState expected)
    {
        using var providers = new ScriptProviders(new Handler(request => Response("{}",
            request.RequestUri!.AbsolutePath == "/api/v1/activity" ? managementStatus : primaryStatus)));
        var result = await providers.FetchAsync("openrouter", key => key == "OPENROUTER_MANAGEMENT_API_KEY" ? "fixture-management" : Setting("openrouter", key), null, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State);
    }
    [Fact]
    public async Task AuthObservationDoesNotLeakBetweenFetchesOnSameInstance()
    {
        var status = 401;
        using var providers = new ScriptProviders(new Handler(_ => Response("{}", status)));
        var first = await providers.FetchAsync("synthetic", _ => "fixture-secret", null, TestContext.Current.CancellationToken);
        status = 503;
        var second = await providers.FetchAsync("synthetic", _ => "fixture-secret", null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, first.State);
        Assert.Equal(ReadingState.Error, second.State);
    }
    [Fact]
    public async Task CallerCancellationWinsOverPreviousOptionalAuthFailure()
    {
        using var cancellation = new CancellationTokenSource();
        using var providers = new ScriptProviders(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/credits", StringComparison.Ordinal)) return Response("{}", 401);
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => providers.FetchAsync("openrouter", key => Setting("openrouter", key), null, cancellation.Token));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
