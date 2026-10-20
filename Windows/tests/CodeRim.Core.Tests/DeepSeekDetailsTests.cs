using System.Globalization;
using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;

public sealed class DeepSeekDetailsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-22T12:00:00Z", CultureInfo.InvariantCulture);
    private static readonly int[] MalformedBlocks = [42];
    private const string Balance = """{"code":0,"data":{"biz_code":0,"biz_data":{"normal_wallets":[{"currency":"USD","balance":"12.34"}],"bonus_wallets":[]}}}""";
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static string Envelope(object value) => JsonSerializer.Serialize(new { code = 0, data = new { biz_code = 0, biz_data = value } });
    private static string Amount(DateTimeOffset date, object? number = null, bool lowercase = false) => Envelope(new {
        series = new[] { new { api_key = new { tracking_id = "fixture-key" }, model = "deepseek-fixture", buckets = new[] {
            new { time = date.ToUnixTimeSeconds(), usage = new Dictionary<string, object> {
                [lowercase ? "prompt_cache_hit_token" : "PROMPT_CACHE_HIT_TOKEN"] = number ?? 100,
                ["PROMPT_CACHE_MISS_TOKEN"] = 200, ["RESPONSE_TOKEN"] = 300, [lowercase ? "request" : "REQUEST"] = 7 } } } } }
    });
    private static string Cost(DateTimeOffset date, string? currency = "USD") => Envelope(new {
        data = new[] { new { currency, series = new[] { new { api_key = new { tracking_id = "fixture-key" },
            buckets = new[] { new { time = date.ToUnixTimeSeconds(), cost = "0.6" } } } } } }
    });
    private static string MonthlyAmount(string date) => Envelope(new {
        total = new[] { new { model = "deepseek-fixture", usage = new[] { new { type = "RESPONSE_TOKEN", amount = "600" } } } },
        days = new[] { new { date, data = new[] { new { model = "deepseek-fixture", usage = new[] {
            new { type = "response_token", amount = "600" }, new { type = "request", amount = "7" } } } } } }
    });
    private static string MonthlyCost(string date) => Envelope(new[] { new { currency = "USD",
        days = new[] { new { date, data = new[] { new { model = "deepseek-fixture", usage = new[] {
            new { type = "RESPONSE_TOKEN", amount = "0.6" } } } } } } } });
    [Fact]
    public void PinnedRollingRowsAndGroupReachTheReadingContract()
    {
        var rows = DeepSeekUsageDetails.ParseByKey(Json(Amount(Now)), Json(Cost(Now)), Now);
        Assert.Equal(["Today", "Last 30 days", "Requests", "API keys", "Top model"], rows.Select(row => row.Name));
        Assert.Equal("$0.6000 · 600 tokens", rows[0].DisplayValue); Assert.Equal("7", rows[2].DisplayValue);
        Assert.Equal("1", rows[3].DisplayValue); Assert.All(rows, row => { Assert.Equal("Usage", row.Group); Assert.Null(row.UsedPercent); });
        var roundTrip = JsonSerializer.Deserialize<LimitWindow>(JsonSerializer.Serialize(rows[0]));
        Assert.Equal(rows[0], roundTrip);
        Assert.DoesNotContain("Group", JsonSerializer.Serialize(new LimitWindow("balance", "Balance")), StringComparison.Ordinal);
    }
    [Fact]
    public void MonthlyPublicParserUsesUtcEvenAtTheLocalMonthBoundary()
    {
        var local = DateTimeOffset.Parse("2026-09-01T00:30:00+14:00", CultureInfo.InvariantCulture);
        var rows = DeepSeekUsageDetails.ParseMonthly(Json(MonthlyAmount("2026-08-31")), Json(MonthlyCost("2026-08-31")), local);
        Assert.Equal(["Today", "This month", "Requests", "Top model"], rows.Select(row => row.Name));
        Assert.Equal("$0.6000 · 600 tokens", rows[0].DisplayValue); Assert.Equal("7", rows[2].DisplayValue);
    }
    [Fact]
    public void LowercaseCategoriesPreserveCounts()
    {
        var rows = DeepSeekUsageDetails.ParseByKey(Json(Amount(Now, lowercase: true)), Json(Cost(Now)), Now);
        Assert.Equal("$0.6000 · 600 tokens", rows[0].DisplayValue); Assert.Equal("7", rows[2].DisplayValue);
    }
    [Theory]
    [InlineData("1e-100")]
    [InlineData("-1e-100")]
    [InlineData("0.99999999999999999999999999999999999")]
    [InlineData("1.1")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("NaN")]
    [InlineData("1e999999")]
    public void CountsCannotRoundUnderflowOrOverflowIntoSuccessfulUsage(string number)
        => Assert.Throws<InvalidDataException>(() => DeepSeekUsageDetails.ParseByKey(Json(Amount(Now, number)), Json(Cost(Now)), Now));
    [Theory]
    [InlineData("\"0\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void MalformedStatusRemainsAHandledDataError(string code)
        => Assert.Throws<InvalidDataException>(() => DeepSeekUsageDetails.ParseByKey(Json(Amount(Now).Replace("\"code\":0", "\"code\":" + code, StringComparison.Ordinal)), Json(Cost(Now)), Now));
    [Fact]
    public void UnknownCurrencyDoesNotInventDollarCosts()
    {
        var rows = DeepSeekUsageDetails.ParseByKey(Json(Amount(Now)), Json(Cost(Now, null)), Now);
        Assert.Equal("— · 600 tokens", rows[0].DisplayValue); Assert.Equal("— · 600 tokens", rows[1].DisplayValue);
    }
    [Fact]
    public void MonthlyMalformedCostBlockDoesNotBecomeZeroSpend()
        => Assert.Throws<InvalidDataException>(() => DeepSeekUsageDetails.ParseMonthly(Json(MonthlyAmount("2026-09-22")), Json(Envelope(MalformedBlocks)), Now));
    [Fact]
    public void AuthRefusalInEitherEnvelopePrecedesOtherParseErrors()
        => Assert.Throws<UnauthorizedAccessException>(() => DeepSeekUsageDetails.ParseByKey(Json("{}"), Json("""{"code":40003,"data":null}"""), Now));
    [Theory]
    [InlineData("""{"code":40002,"code":0,"data":null}""")]
    [InlineData("""{"code":0,"data":{"biz_code":40003,"biz_code":0}}""")]
    public void ARefusalInAnAmbiguousEnvelopeCannotEnableFallback(string refusal)
        => Assert.Throws<UnauthorizedAccessException>(() => DeepSeekUsageDetails.ParseByKey(Json("{}"), Json(refusal), Now));
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }
    private static HttpResponseMessage Reply(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(content) };
    [Theory]
    [InlineData("web", false, 1)]
    [InlineData("api", true, 1)]
    [InlineData("web", true, 3)]
    public async Task OptionalUsageUsesOnlyItsSelectedWebSession(string source, bool enabled, int expected)
    {
        var calls = new List<string>();
        using var reader = new HttpProviders(new Handler(request => {
            calls.Add(request.RequestUri!.AbsoluteUri);
            Assert.Equal("selected-session", request.Headers.Authorization?.Parameter);
            if (request.RequestUri.Host == "api.deepseek.com")
                return Task.FromResult(Reply("""{"is_available":true,"balance_infos":[{"currency":"USD","total_balance":"12.34"}]}"""));
            Assert.Equal("platform.deepseek.com", request.RequestUri.Host);
            Assert.Equal("web", Assert.Single(request.Headers.GetValues("x-client-platform")));
            var content = request.RequestUri.AbsolutePath.EndsWith("get_user_summary", StringComparison.Ordinal) ? Balance
                : request.RequestUri.AbsolutePath.EndsWith("/amount", StringComparison.Ordinal) ? Amount(DateTimeOffset.Now) : Cost(DateTimeOffset.Now);
            return Task.FromResult(Reply(content));
        }));
        var reading = await reader.FetchAsync("deepseek", "selected-session", key => key == "DEEPSEEK_USAGE_SOURCE" ? source : enabled.ToString(), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(expected, calls.Count);
        Assert.Equal(expected == 3 ? 6 : 1, reading.Windows.Count);
    }
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task OptionalRefusalCannotBeHiddenByTheOtherResponseParseFailure(int status)
    {
        var calls = 0;
        using var reader = new HttpProviders(new Handler(request => {
            Interlocked.Increment(ref calls);
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("get_user_summary", StringComparison.Ordinal) ? Reply(Balance)
                : request.RequestUri.AbsolutePath.EndsWith("/amount", StringComparison.Ordinal) ? Reply("{")
                : Reply("{}", (HttpStatusCode)status));
        }));
        var reading = await reader.FetchAsync("deepseek", "session", key => key == "DEEPSEEK_USAGE_SOURCE" ? "web" : "true", TestContext.Current.CancellationToken);
        Assert.Equal(3, calls); Assert.Equal(ReadingState.Partial, reading.State); Assert.Single(reading.Windows);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonAuthRefusalCannotBeHiddenByTheOtherResponseFailure(bool reverse)
    {
        var calls = 0;
        using var reader = new HttpProviders(new Handler(request => {
            Interlocked.Increment(ref calls);
            if (request.RequestUri!.AbsolutePath.EndsWith("get_user_summary", StringComparison.Ordinal)) return Task.FromResult(Reply(Balance));
            var amount = request.RequestUri.AbsolutePath.EndsWith("/amount", StringComparison.Ordinal);
            return Task.FromResult(Reply(amount != reverse ? "{" : """{"code":40002,"data":null}"""));
        }));
        var reading = await reader.FetchAsync("deepseek", "session", key => key == "DEEPSEEK_USAGE_SOURCE" ? "web" : "true", TestContext.Current.CancellationToken);
        Assert.Equal(3, calls); Assert.Equal(ReadingState.Partial, reading.State); Assert.Single(reading.Windows);
    }

    [Fact]
    public async Task CompatibleMonthlyFallbackUsesTheSameTokenAndKeepsItsPeriodLabel()
    {
        var calls = new List<Uri>();
        using var reader = new HttpProviders(new Handler(request => {
            var uri = request.RequestUri!; calls.Add(uri); Assert.Equal("session", request.Headers.Authorization?.Parameter);
            if (uri.AbsolutePath.EndsWith("get_user_summary", StringComparison.Ordinal)) return Task.FromResult(Reply(Balance));
            if (uri.AbsolutePath.Contains("by_api_key", StringComparison.Ordinal)) return Task.FromResult(Reply("{}", HttpStatusCode.NotFound));
            var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return Task.FromResult(Reply(uri.AbsolutePath.EndsWith("/amount", StringComparison.Ordinal) ? MonthlyAmount(today) : MonthlyCost(today)));
        }));
        var reading = await reader.FetchAsync("deepseek", "session", key => key == "DEEPSEEK_USAGE_SOURCE" ? "web" : "true", TestContext.Current.CancellationToken);
        Assert.Equal(5, calls.Count); Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal("This month", reading.Windows[2].Name); Assert.Equal("$0.6000 · 600 tokens", reading.Windows[1].DisplayValue);
    }
    [Fact]
    public async Task CallerCancellationReturnsWithoutWaitingForAnIgnoringOptionalHandler()
    {
        var optional = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var reader = new HttpProviders(new Handler(request => {
            if (request.RequestUri!.AbsolutePath.EndsWith("get_user_summary", StringComparison.Ordinal)) return Task.FromResult(Reply(Balance));
            optional.TrySetResult(); return response.Task;
        }));
        var task = reader.FetchAsync("deepseek", "session", key => key == "DEEPSEEK_USAGE_SOURCE" ? "web" : "true", cancel.Token);
        await optional.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); cancel.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)); }
        finally { response.TrySetResult(Reply("{}")); }
    }
}
