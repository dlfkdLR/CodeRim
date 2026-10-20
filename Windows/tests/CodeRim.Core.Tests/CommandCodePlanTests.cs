using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;

public sealed class CommandCodePlanTests
{
    [Theory]
    [InlineData("goat_monthly", "GOAT", false)]
    [InlineData(null, null, false)]
    [InlineData("goat_monthly", "GOAT", true)]
    [InlineData(null, null, true)]
    public async Task SuccessfulSubscriptionUpdatesPlanEvenWhenUsageFails(string? plan, string? expected, bool failUsage)
    {
        using var handler = new Handler(plan, failUsage ? "/alpha/usage/summary" : null);
        using var provider = new NativeProviders(handler);
        var reading = await provider.FetchAsync("commandcode", "fixture-key", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(failUsage ? ReadingState.Error : ReadingState.Ready, reading.State);
        Assert.NotNull(reading.AccountPlanUpdate);
        Assert.Equal(expected, reading.AccountPlanUpdate.Plan);
        if (failUsage) Assert.Empty(reading.Windows);
        else Assert.Equal(25, Assert.Single(reading.Windows).UsedPercent);
        Assert.DoesNotContain("AccountPlanUpdate", JsonSerializer.Serialize(reading), StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<ProviderReading>(JsonSerializer.Serialize(reading))!.AccountPlanUpdate);
    }

    [Theory]
    [InlineData("/alpha/whoami")]
    [InlineData("/alpha/billing/credits")]
    [InlineData("/alpha/billing/subscriptions")]
    public async Task FailureBeforeSubscriptionDoesNotClearTheKnownPlan(string failedPath)
    {
        using var handler = new Handler("goat_monthly", failedPath);
        using var provider = new NativeProviders(handler);
        var reading = await provider.FetchAsync("commandcode", "fixture-key", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State);
        Assert.Empty(reading.Windows);
        Assert.Null(reading.AccountPlanUpdate);
    }

    [Theory]
    [InlineData("/alpha/billing/subscriptions", "not JSON", ReadingState.Ready, null)]
    [InlineData("/alpha/billing/subscriptions", "[]", ReadingState.Ready, null)]
    [InlineData("/alpha/billing/subscriptions", "null", ReadingState.Ready, null)]
    [InlineData("/alpha/billing/credits", "not JSON", ReadingState.Error, "GOAT")]
    [InlineData("/alpha/billing/credits", "[]", ReadingState.Error, "GOAT")]
    [InlineData("/alpha/usage/summary", "not JSON", ReadingState.Error, "GOAT")]
    [InlineData("/alpha/whoami", "not JSON", ReadingState.Ready, "GOAT")]
    public async Task BodiesAreValidatedAtTheReferenceStage(string changedPath, string body, ReadingState state, string? expected)
    {
        using var handler = new Handler("goat_monthly", null) { ChangedPath = changedPath, Body = body };
        using var provider = new NativeProviders(handler);
        var reading = await provider.FetchAsync("commandcode", "fixture-key", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(state, reading.State);
        Assert.NotNull(reading.AccountPlanUpdate); Assert.Equal(expected, reading.AccountPlanUpdate.Plan);
        if (state == ReadingState.Error) Assert.Empty(reading.Windows);
        else Assert.Equal(25, Assert.Single(reading.Windows).UsedPercent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ReadingState.NeedsAuth)]
    [InlineData(HttpStatusCode.TooManyRequests, ReadingState.Unavailable)]
    public async Task LateAuthenticationOrRateLimitFailureRetainsTheSuccessfulPlanUpdate(HttpStatusCode status, ReadingState state)
    {
        using var handler = new Handler("goat_monthly", "/alpha/usage/summary") { FailureStatus = status };
        using var provider = new NativeProviders(handler);
        var reading = await provider.FetchAsync("commandcode", "fixture-key", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(state, reading.State); Assert.Empty(reading.Windows);
        Assert.Equal("GOAT", reading.AccountPlanUpdate?.Plan);
    }

    [Fact]
    public async Task AUsageTimeoutKeepsTheUpdateButCallerCancellationPropagates()
    {
        using var handler = new Handler("goat_monthly", null)
        { OnRequest = path => { if (path == "/alpha/usage/summary") throw new OperationCanceledException("Synthetic timeout"); } };
        using var provider = new NativeProviders(handler);
        var reading = await provider.FetchAsync("commandcode", "fixture-key", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Equal("GOAT", reading.AccountPlanUpdate?.Plan);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        handler.OnRequest = path => { if (path == "/alpha/usage/summary") caller.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchAsync("commandcode", "fixture-key", _ => null, caller.Token));
    }

    [Fact]
    public async Task InvalidUtf8SubscriptionDoesNotPublishAPlanUpdate()
    {
        using var handler = new Handler("goat_monthly", null) { ChangedPath = "/alpha/billing/subscriptions", InvalidUtf8 = true };
        using var provider = new NativeProviders(handler);
        var reading = await provider.FetchAsync("commandcode", "fixture-key", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Null(reading.AccountPlanUpdate);
    }

    [Theory]
    [InlineData("{}", "{\"credits\":{\"monthlyCredits\":75}}", 0d)]
    [InlineData("{\"totalCost\":25}", "{}", 100d)]
    [InlineData("{}", "{\"windowLimits\":{\"weekly\":{\"cap\":100,\"used\":25}}}", null)]
    public void MonthlyAmountsDefaultToZeroAndGuardOptionalWindows(string usage, string credits, double? expected)
    {
        var reading = NativeProviders.Parse("commandcode", new Dictionary<string, JsonElement>
        { ["usage"] = JsonSerializer.Deserialize<JsonElement>(usage), ["credits"] = JsonSerializer.Deserialize<JsonElement>(credits) });
        Assert.Equal(expected.HasValue ? ReadingState.Ready : ReadingState.Unavailable, reading.State);
        if (expected.HasValue) Assert.Equal(expected, Assert.Single(reading.Windows).UsedPercent);
        else Assert.Empty(reading.Windows);
    }

    private sealed class Handler(string? plan, string? failedPath) : HttpMessageHandler
    {
        internal string? ChangedPath { get; init; }
        internal string? Body { get; init; }
        internal bool InvalidUtf8 { get; init; }
        internal HttpStatusCode FailureStatus { get; init; } = HttpStatusCode.ServiceUnavailable;
        internal Action<string>? OnRequest { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.commandcode.ai", request.RequestUri!.Host);
            Assert.Equal("Bearer fixture-key", request.Headers.Authorization!.ToString());
            var path = request.RequestUri.AbsolutePath;
            OnRequest?.Invoke(path); cancellationToken.ThrowIfCancellationRequested();
            var content = path switch
            {
                "/alpha/whoami" => """{"org":{"id":"fixture-org"}}""",
                "/alpha/billing/credits" => """{"credits":{"monthlyCredits":75}}""",
                "/alpha/billing/subscriptions" => JsonSerializer.Serialize(new { data = new { planId = plan } }),
                "/alpha/usage/summary" => """{"totalCost":25}""",
                _ => throw new InvalidOperationException("Unexpected fixture endpoint.")
            };
            return Task.FromResult(new HttpResponseMessage(path == failedPath ? FailureStatus : HttpStatusCode.OK)
                { Content = path == ChangedPath && InvalidUtf8 ? new ByteArrayContent([0xff]) : new StringContent(path == ChangedPath ? Body! : content) });
        }
    }
}
