using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using Xunit;

namespace CodeRim.Core.Tests;

public sealed class MiniMaxWebTests
{
    private const string Quota = """{"model_remains":[{"model_name":"video","current_interval_remaining_percent":30},{"model_name":"general","current_interval_remaining_percent":96,"current_weekly_remaining_percent":99}]}""";
    private const string EmptyBilling = """{"base_resp":{"status_code":0},"charge_records":[],"total_cnt":0}""";
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request); }
    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    [Theory]
    [InlineData("HERTZ-SESSION=fixture; minimax_group_id_v2=123")]
    [InlineData("Cookie: HERTZ-SESSION=fixture; minimax_group_id_v2=123")]
    [InlineData("Authorization: Bearer fixture-token\r\nCookie: HERTZ-SESSION=fixture; minimax_group_id_v2=123\r\nX-Group-Id: 123")]
    [InlineData("'HERTZ-SESSION=fixture; minimax_group_id_v2=123'")]
    [InlineData("curl 'https://platform.minimax.io/test?GroupId=123' -b 'HERTZ-SESSION=fixture; minimax_group_id_v2=123'")]
    [InlineData("curl 'https://platform.minimax.io/test' --header='Cookie: HERTZ-SESSION=fixture; minimax_group_id_v2=123'")]
    public void CookieFormatsKeepOneAccount(string input)
    { var value = MiniMaxAuthentication.Parse(input, "global"); Assert.NotNull(value); Assert.Equal("123", value.Group); Assert.Equal("HERTZ-SESSION=fixture; minimax_group_id_v2=123", value.Cookie); }
    [Theory]
    [InlineData("")]
    [InlineData("HERTZ-SESSION=a\nInjected: true")]
    [InlineData("HERTZ-SESSION=a; HERTZ-SESSION=b")]
    [InlineData("curl 'https://platform.minimaxi.com/test' -b 'HERTZ-SESSION=cn'")]
    [InlineData("curl 'https://example.test/' -b 'HERTZ-SESSION=a'")]
    [InlineData("curl 'https://platform.minimax.io/test?GroupId=1' -b 'HERTZ-SESSION=a; minimax_group_id_v2=2'")]
    [InlineData("curl 'https://platform.minimax.io/' -H 'Cookie: HERTZ-SESSION=a' -H 'Cookie: HERTZ-SESSION=b'")]
    [InlineData("curl 'https://platform.minimax.io/' -b 'HERTZ-SESSION=a' -H 'Authorization: Basic x'")]
    [InlineData("curl 'https://platform.minimax.io/' -b 'HERTZ-SESSION=a' -H 'Authorization: Bearer a' -H 'Authorization: Bearer b'")]
    [InlineData("curl 'https://platform.minimax.io/' -b 'HERTZ-SESSION=a")]
    public void InvalidOrConflictingCaptureIsRejected(string input) => Assert.Null(MiniMaxAuthentication.Parse(input, "global"));
    [Fact]
    public void CaptureIsDataAndPreservesSameCaptureBearer()
    {
        var input = "curl 'https://platform.minimax.io/?GroupId=123' -H 'Cookie: HERTZ-SESSION=$(not-executed)' -H 'Authorization: Bearer fixture'";
        var value = MiniMaxAuthentication.Parse(input, "global");
        Assert.NotNull(value); Assert.Equal("fixture", value.Bearer); Assert.Contains("$(not-executed)", value.Cookie);
        Assert.Null(MiniMaxAuthentication.Parse(new string('x', 65537), "global"));
    }
    [Theory]
    [InlineData("<html><body>Please sign in</body></html>")]
    [InlineData("<html><script id='__NEXT_DATA__'>{\"baseResp\":{\"status_code\":1004}}</script><body>Coding Plan Max 0% used</body></html>")]
    public void SemanticAuthCannotFallThroughToCoarseQuota(string html) => Assert.ThrowsAny<Exception>(() => NativeProviders.ParseMiniMaxWebPlan(html));
    [Fact]
    public void InertLoginTranslationsDoNotBlockStructuredQuota()
    {
        var html = "<script>var message='log in';</script><!-- sign in --><style>.sign-in{}</style><script id='__NEXT_DATA__'>" + Quota + "</script>";
        var reading = NativeProviders.ParseMiniMaxWebPlan(html); Assert.Equal(4, reading.Headline!.UsedPercent); Assert.Equal(1, reading.Windows[1].UsedPercent);
    }
    [Theory]
    [InlineData("0% used", "5 hours", 300)]
    [InlineData("used 25%", "90 minutes", 90)]
    [InlineData("75% used", "1.5 days", 2160)]
    public void CoarseHtmlHasMeasuredUsageDurationAndRelativeReset(string used, string duration, int minutes)
    {
        var now = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
        var result = NativeProviders.ParseMiniMaxWebPlan("Coding Plan Max Available usage: 1,000 prompts / " + duration + " " + used + " resets in 4 minutes", now);
        Assert.Equal(minutes, result.Headline!.DurationMinutes); Assert.Equal(now.AddMinutes(4), result.Headline.ResetsAt);
    }
    [Fact]
    public void PlanNameAloneDoesNotInventZeroQuota() => Assert.Throws<InvalidDataException>(() => NativeProviders.ParseMiniMaxWebPlan("Coding Plan Max"));
    [Theory]
    [InlineData("global", "minimax.io")]
    [InlineData("cn", "minimaxi.com")]
    public async Task StructuredPlanSkipsRemainsAndKeepsRegionAndHeaderContract(string region, string domain)
    {
        var paths = new List<string>();
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content);
            Assert.EndsWith("." + domain, request.RequestUri!.Host); paths.Add(request.RequestUri.AbsolutePath);
            Assert.Equal("HERTZ-SESSION=fixture; minimax_group_id_v2=123", request.Headers.GetValues("Cookie").Single());
            if (request.RequestUri.AbsolutePath.Contains("cycle_audio", StringComparison.Ordinal))
            {
                Assert.Null(request.Headers.Authorization); Assert.Equal("123", request.Headers.GetValues("x-group-id").Single());
                return Task.FromResult(Ok("""{"data":{"current_subscribe":{"current_subscribe_title":"Current Max"},"packages":[{"title":"Sale Ultra"}]}}"""));
            }
            Assert.Equal("fixture-bearer", request.Headers.Authorization!.Parameter);
            return Task.FromResult(Ok(request.RequestUri.AbsolutePath == "/account/amount" ? EmptyBilling : "<script id='__NEXT_DATA__'>" + Quota + "</script>"));
        });
        using var provider = new NativeProviders(handler);
        var profile = new MiniMaxWebCredential(region, "HERTZ-SESSION=fixture; minimax_group_id_v2=123", "fixture-bearer", "123");
        var result = await provider.FetchMiniMaxWebAsync(profile, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, result.State); Assert.Equal(4, result.Headline!.UsedPercent); Assert.Equal("Current Max", result.Plan);
        Assert.Equal(3, paths.Count); Assert.DoesNotContain(paths, path => path.EndsWith("/remains", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData(401, ReadingState.NeedsAuth)]
    [InlineData(403, ReadingState.NeedsAuth)]
    [InlineData(429, ReadingState.Unavailable)]
    [InlineData(500, ReadingState.Error)]
    public async Task PrimaryHttpFailureStopsWithoutFallback(int status, ReadingState state)
    {
        var calls = 0;
        using var handler = new Handler(_ => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)); });
        using var provider = new NativeProviders(handler);
        var result = await provider.FetchMiniMaxWebAsync(new("global", "HERTZ-SESSION=a"), TestContext.Current.CancellationToken);
        Assert.Equal(state, result.State); Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData(404, true)]
    [InlineData(405, true)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(429, false)]
    [InlineData(503, false)]
    public async Task RemainsFallbackNeverCrossesRegionOrAuthenticationFailure(int status, bool fallback)
    {
        var www = 0;
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/coding-plan", StringComparison.Ordinal)) return Task.FromResult(Ok("<html>unrecognized page</html>"));
            if (request.RequestUri.AbsolutePath == "/account/amount") return Task.FromResult(Ok(EmptyBilling));
            if (request.RequestUri.Host == "www.minimaxi.com") { www++; return Task.FromResult(Ok(Quota)); }
            Assert.Equal("platform.minimaxi.com", request.RequestUri.Host);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        });
        using var provider = new NativeProviders(handler);
        var result = await provider.FetchMiniMaxWebAsync(new("cn", "HERTZ-SESSION=a"), TestContext.Current.CancellationToken);
        Assert.Equal(fallback ? 1 : 0, www); Assert.Equal(fallback, result.State == ReadingState.Ready);
    }
    [Fact]
    public async Task ScopedBrowserUsesOnlyItsCurrentHostSessionAsBearer()
    {
        var jar = new BrowserCookieJar([new("HERTZ-SESSION", "fixture", "platform.minimax.io", "/", true, true, 0)], ["minimax.io"]);
        using var handler = new Handler(request =>
        {
            Assert.Equal("platform.minimax.io", request.RequestUri!.Host);
            Assert.Equal("fixture", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Ok(request.RequestUri.AbsolutePath == "/account/amount" ? EmptyBilling : Quota));
        });
        using var provider = new NativeProviders(handler);
        var result = await provider.FetchMiniMaxWebAsync(new("global", "HERTZ-SESSION=fixture", BrowserState: jar.Serialize()), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, result.State);
    }
    [Theory]
    [InlineData(false, ReadingState.Partial)]
    [InlineData(true, ReadingState.NeedsAuth)]
    public async Task OptionalBillingAuthOnlyInvalidatesAnExplicitBearer(bool bearer, ReadingState state)
    {
        using var handler = new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath == "/account/amount" ? new(HttpStatusCode.Forbidden) : Ok(Quota)));
        using var provider = new NativeProviders(handler);
        var result = await provider.FetchMiniMaxWebAsync(new("global", "HERTZ-SESSION=a", bearer ? "fixture" : null), TestContext.Current.CancellationToken);
        Assert.Equal(state, result.State);
    }
    [Fact]
    public async Task BillingUsesTokenCashAndDatePriorityWithoutInferringCurrency()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var rows = JsonSerializer.Serialize(new { total_cnt = 5, charge_records = new object[] {
            new { consume_token = 100, consume_input_token = 90, consume_output_token = 80, consume_cash_after_voucher = 0.5, consume_cash = 1.0, ymd = today, result = "success" },
            new { consume_token = 0, consume_input_token = 20, consume_output_token = 30, consume_cash_after_voucher = 0.0, consume_cash = 1.0, ymd = today, result = " SUCCESS " },
            new { consume_token = 9999, ymd = today, result = "FAILED" },
            new { consume_token = 8888, ymd = today, created_at = 0 },
            new { consume_token = 7777, ymd = "2999-01-01" }
        }});
        using var handler = new Handler(request => Task.FromResult(Ok(request.RequestUri!.AbsolutePath == "/account/amount" ? rows : Quota)));
        using var provider = new NativeProviders(handler);
        var result = await provider.FetchMiniMaxWebAsync(new("global", "HERTZ-SESSION=a"), TestContext.Current.CancellationToken);
        var activity = Assert.Single(result.Windows, window => window.Id == "account.today");
        Assert.Equal(150, activity.UsedCount); Assert.Contains("currency unspecified", activity.DisplayValue); Assert.DoesNotContain("USD", activity.DisplayValue);
    }
    [Fact]
    public async Task RepeatedBillingPageIsBoundedAndPartial()
    {
        var count = 0; var today = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var row = JsonSerializer.Serialize(new { total_cnt = 9999, charge_records = new[] { new { consume_token = 3, ymd = today } } });
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/account/amount") { count++; return Task.FromResult(Ok(row)); }
            return Task.FromResult(Ok(Quota));
        });
        using var provider = new NativeProviders(handler);
        var result = await provider.FetchMiniMaxWebAsync(new("global", "HERTZ-SESSION=a"), TestContext.Current.CancellationToken);
        Assert.Equal(2, count); Assert.Equal(ReadingState.Partial, result.State);
        Assert.Equal(3, result.Windows.First(window => window.Id == "account.today").UsedCount);
    }
    [Fact]
    public async Task Optional429DoesNotThrottlePrimaryRefresh()
    {
        var plans = 0;
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/account/amount") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            plans++; return Task.FromResult(Ok(Quota));
        });
        using var provider = new NativeProviders(handler); var profile = new MiniMaxWebCredential("global", "HERTZ-SESSION=a");
        Assert.Equal(ReadingState.Partial, (await provider.FetchMiniMaxWebAsync(profile, TestContext.Current.CancellationToken)).State);
        Assert.Equal(ReadingState.Partial, (await provider.FetchMiniMaxWebAsync(profile, TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, plans);
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DifferentHostAccountNeverEnrichesSelectedBrowserQuota(bool coarse, bool differentGroup)
    {
        var cookies = new[] {
            new BrowserCookie("HERTZ-SESSION", "account-a", "platform.minimax.io", "/", true, true, 0),
            new BrowserCookie("minimax_group_id_v2", "111", "platform.minimax.io", "/", true, true, 0),
            new BrowserCookie("HERTZ-SESSION", "account-b", "www.minimax.io", "/", true, true, 0),
            new BrowserCookie("minimax_group_id_v2", differentGroup ? "222" : "111", "www.minimax.io", "/", true, true, 0)
        };
        var jar = new BrowserCookieJar(cookies, ["minimax.io"]); var outside = 0;
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "www.minimax.io") { outside++; return Task.FromResult(Ok(Quota)); }
            if (request.RequestUri.AbsolutePath.EndsWith("/remains", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(Ok(request.RequestUri.AbsolutePath == "/account/amount" ? EmptyBilling : coarse ? "Coding Plan Max 25% used" : Quota));
        });
        using var provider = new NativeProviders(handler);
        var selected = MiniMaxAuthentication.Parse(jar.Header(MiniMaxAuthentication.PlanUri("global"), DateTimeOffset.Now), "global")! with { BrowserState = jar.Serialize() };
        var result = await provider.FetchMiniMaxWebAsync(selected, TestContext.Current.CancellationToken);
        Assert.Equal(0, outside); Assert.Equal(coarse ? ReadingState.NeedsAuth : ReadingState.Partial, result.State);
        if (!coarse) Assert.Equal(4, result.Headline!.UsedPercent);
    }
    [Fact]
    public async Task BrowserSessionBearerRetriesCookieOnlyAfterAuth()
    {
        var jar = new BrowserCookieJar([new("HERTZ-SESSION", "fixture", "platform.minimax.io", "/", true, true, 0)], ["minimax.io"]);
        var bearerCalls = 0; var cookieCalls = 0;
        using var handler = new Handler(request =>
        {
            if (request.Headers.Authorization is not null) { bearerCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }
            cookieCalls++; return Task.FromResult(Ok(request.RequestUri!.AbsolutePath == "/account/amount" ? EmptyBilling : Quota));
        });
        using var provider = new NativeProviders(handler);
        var result = await provider.FetchMiniMaxWebAsync(new("global", "HERTZ-SESSION=fixture", BrowserState: jar.Serialize()), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, result.State); Assert.Equal(1, bearerCalls); Assert.Equal(2, cookieCalls);
    }
    [Theory]
    [InlineData("<!--", "-->")]
    [InlineData("<template>", "</template>")]
    [InlineData("<template><template>", "</template></template>")]
    [InlineData("<textarea>", "</textarea>")]
    public void InertNextDataNeverOverridesActiveQuota(string open, string close)
    {
        var hidden = "<script id='__NEXT_DATA__'>{\"model_remains\":[{\"model_name\":\"general\",\"current_interval_remaining_percent\":30}]}</script>";
        var active = "<script id='__NEXT_DATA__'>" + Quota + "</script>";
        Assert.Equal(4, NativeProviders.ParseMiniMaxWebPlan(open + hidden + close + active).Headline!.UsedPercent);
    }
    [Fact]
    public void LegacySingleQuotaWithoutModelNameRemainsMeasurable()
    {
        var html = """<script id="__NEXT_DATA__">{"props":{"pageProps":{"data":{"model_remains":[{"current_interval_total_count":1000,"current_interval_usage_count":250}]}}}}</script>""";
        Assert.Equal(75, NativeProviders.ParseMiniMaxWebPlan(html).Headline!.UsedPercent);
    }
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task BlankResultFallsBackToFailureStatus(string result)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var rows = JsonSerializer.Serialize(new { total_cnt = 2, charge_records = new[] {
            new { consume_token = 5, ymd = today, result = "SUCCESS", status = "SUCCESS" },
            new { consume_token = 999, ymd = today, result, status = "FAILED" }
        }});
        using var handler = new Handler(request => Task.FromResult(Ok(request.RequestUri!.AbsolutePath == "/account/amount" ? rows : Quota)));
        using var provider = new NativeProviders(handler);
        var reading = await provider.FetchMiniMaxWebAsync(new("global", "HERTZ-SESSION=a"), TestContext.Current.CancellationToken);
        Assert.Equal(5, Assert.Single(reading.Windows, window => window.Id == "account.today").UsedCount);
    }
    [Fact]
    public async Task CatalogSubscriptionObjectCannotBecomeCurrentPlan()
    {
        using var handler = new Handler(request => Task.FromResult(Ok(request.RequestUri!.AbsolutePath == "/account/amount" ? EmptyBilling
            : request.RequestUri.AbsolutePath.Contains("cycle_audio", StringComparison.Ordinal)
                ? """{"data":{"packages":[{"current_subscribe":{"title":"CATALOG"}}]}}""" : Quota)));
        using var provider = new NativeProviders(handler);
        var reading = await provider.FetchMiniMaxWebAsync(new("global", "HERTZ-SESSION=a", Group: "123"), TestContext.Current.CancellationToken);
        Assert.Null(reading.Plan); Assert.Equal(4, reading.Headline!.UsedPercent);
    }
    [Fact]
    public async Task SemanticallyRepeatedPageCannotDoubleBilling()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture); var pages = 0;
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/account/amount") return Task.FromResult(Ok(Quota));
            pages++;
            var row = pages % 2 == 1 ? $"{{\"consume_token\":3,\"ymd\":\"{today}\"}}" : $"{{ \"ymd\" : \"{today}\", \"consume_token\": 3.0 }}";
            return Task.FromResult(Ok("{\"total_cnt\":999,\"charge_records\":[" + row + "]}"));
        });
        using var provider = new NativeProviders(handler);
        var reading = await provider.FetchMiniMaxWebAsync(new("global", "HERTZ-SESSION=a"), TestContext.Current.CancellationToken);
        Assert.Equal(2, pages); Assert.Equal(ReadingState.Partial, reading.State);
        Assert.Equal(3, Assert.Single(reading.Windows, window => window.Id == "account.today").UsedCount);
    }
    [Theory]
    [InlineData("Auto", "web", ReadingState.Ready)]
    [InlineData(" WEB ", "web", ReadingState.Ready)]
    [InlineData("API", "api", ReadingState.Ready)]
    [InlineData("invalid", "none", ReadingState.Error)]
    public async Task DirectCoreEntryNormalizesSourceAndKeepsApiSeparate(string mode, string expected, ReadingState state)
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            calls++;
            var web = request.RequestUri!.Host == "platform.minimax.io";
            Assert.Equal(expected == "web", web);
            if (!web) Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(Ok(request.RequestUri.AbsolutePath == "/account/amount" ? EmptyBilling : Quota));
        });
        using var provider = new NativeProviders(handler);
        var reading = await provider.FetchAsync("minimax", "fixture-api", key => key == "MINIMAX_USAGE_SOURCE" ? mode : key == "MINIMAX_COOKIE" ? "HERTZ-SESSION=a" : null, TestContext.Current.CancellationToken);
        Assert.Equal(state, reading.State); if (expected == "none") Assert.Equal(0, calls);
    }
    [Fact]
    public async Task DirectApiRegionCannotChangeBetweenFallbackEndpoints()
    {
        var region = "global"; var calls = 0;
        using var handler = new Handler(request =>
        {
            calls++; Assert.Equal("api.minimax.io", request.RequestUri!.Host); region = "cn";
            return Task.FromResult(calls == 1 ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok(Quota));
        });
        using var provider = new NativeProviders(handler);
        var reading = await provider.FetchAsync("minimax", "fixture", key => key == "MINIMAX_REGION" ? region : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(2, calls);
    }
    [Fact]
    public async Task CallerCancellationDoesNotTryAnotherEndpoint()
    {
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var handler = new Handler(_ => { requests++; called.TrySetResult(); return response.Task; });
        using var provider = new NativeProviders(handler); using var cancellation = new CancellationTokenSource();
        var pending = provider.FetchMiniMaxWebAsync(new("global", "HERTZ-SESSION=a"), cancellation.Token);
        await called.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        response.SetResult(Ok(Quota)); Assert.Equal(1, requests);
    }
}
