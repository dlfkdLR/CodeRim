using System.Net;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class AlibabaCodingPlanWebTests
{
    private const string Cookie = "login_aliyunid_ticket=fixture-ticket; login_aliyunid_pk=account-a; sec_token=cookie-sec; login_aliyunid_csrf=csrf; cna=anonymous";
    private const string Quota = """{"data":{"codingPlanInstanceInfos":[{"status":"ACTIVE","planName":"Pro","codingPlanQuotaInfo":{"per5HourUsedQuota":25,"per5HourTotalQuota":100,"perWeekUsedQuota":100,"perWeekTotalQuota":1000,"perBillMonthUsedQuota":200,"perBillMonthTotalQuota":2000}}]}}""";
    private static ProviderReading Parse(string json, DateTimeOffset? time = null, bool web = true)
    { using var document = JsonDocument.Parse(json); return AlibabaCodingPlanUsage.Parse(document.RootElement, time, web); }
    private static HttpResponseMessage Reply(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request, token); }

    [Theory]
    [InlineData("intl")]
    [InlineData("cn")]
    public async Task UsesFixedRegionalWebFormWithoutApiCredentials(string region)
    {
        var config = AlibabaCodingPlanAuthentication.Region(region)!; var calls = 0;
        using var provider = new NativeProviders(new Handler(async (request, token) =>
        {
            calls++; Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("x-api-key"));
            Assert.Equal(Cookie, request.Headers.GetValues("Cookie").Single());
            if (request.Method == HttpMethod.Get)
            { Assert.Equal(config.Dashboard, request.RequestUri); return Reply("<script>var cfg={SEC_TOKEN:'html+&=sec'}</script>"); }
            Assert.Equal(config.Quota, request.RequestUri); Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(config.Origin, request.Headers.GetValues("Origin").Single());
            Assert.Equal(config.Dashboard.GetLeftPart(UriPartial.Query), request.Headers.Referrer!.AbsoluteUri);
            Assert.Equal("csrf", request.Headers.GetValues("x-xsrf-token").Single());
            Assert.Equal("csrf", request.Headers.GetValues("x-csrf-token").Single());
            Assert.Equal("XMLHttpRequest", request.Headers.GetValues("X-Requested-With").Single());
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            var form = (await request.Content.ReadAsStringAsync(token)).Split('&').Select(x => x.Split('=', 2))
                .ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));
            Assert.Equal(3, form.Count); Assert.Equal(config.Region, form["region"]); Assert.Equal("html+&=sec", form["sec_token"]);
            using var json = JsonDocument.Parse(form["params"]); var root = json.RootElement;
            Assert.Equal(AlibabaCodingPlanAuthentication.QuotaApi, root.GetProperty("Api").GetString()); Assert.Equal("1.0", root.GetProperty("V").GetString());
            var data = root.GetProperty("Data"); var query = data.GetProperty("queryCodingPlanInstanceInfoRequest");
            Assert.True(query.GetProperty("onlyLatestOne").GetBoolean()); Assert.Equal(config.Commodity, query.GetProperty("commodityCode").GetString());
            Assert.Equal(config.Site, data.GetProperty("cornerstoneParam").GetProperty("consoleSite").GetString());
            Assert.Equal("anonymous", data.GetProperty("cornerstoneParam").GetProperty("X-Anonymous-Id").GetString());
            return Reply(Quota);
        }));
        var reading = await provider.FetchAsync("alibaba", Cookie, key => key == "ALIBABA_CODING_PLAN_SOURCE" ? "web" : key == "ALIBABA_CODING_PLAN_REGION" ? region : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(2, calls); Assert.Equal(3, reading.Windows.Count);
        Assert.Equal(25, reading.Headline!.UsedCount); Assert.Equal(75, reading.Headline.RemainingCount); Assert.Equal(43200, reading.Windows[2].DurationMinutes);
    }
    [Theory]
    [InlineData("userinfo")]
    [InlineData("cookie")]
    public async Task DiscoversSecurityTokenWithoutExecutingPage(string source)
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(async (request, token) =>
        {
            calls++;
            if (request.Method == HttpMethod.Get) return Reply(request.RequestUri!.AbsolutePath == "/tool/user/info.json"
                ? source == "userinfo" ? """{"data":"{\"secToken\":\"nested-sec\"}"}""" : "{}" : "<html>no executable discovery</html>");
            Assert.Contains(source == "userinfo" ? "sec_token=nested-sec" : "sec_token=cookie-sec", await request.Content!.ReadAsStringAsync(token));
            return Reply(Quota);
        }));
        Assert.Equal(ReadingState.Ready, (await provider.FetchAlibabaCodingWebAsync(Cookie, "intl", token: TestContext.Current.CancellationToken)).State);
        Assert.Equal(3, calls);
    }
    [Fact]
    public async Task ScopedGatewayCookieDoesNotGetSentToDashboard()
    {
        var calls = 0; var resolutions = new List<Uri>();
        using var provider = new NativeProviders(new Handler((request, _) =>
        { calls++; Assert.Equal("bailian-singapore-cs.alibabacloud.com", request.RequestUri!.Host); Assert.Equal(HttpMethod.Post, request.Method); return Task.FromResult(Reply(Quota)); }));
        var reading = await provider.FetchAlibabaCodingWebAsync("another-account", "intl", uri =>
        { resolutions.Add(uri); return uri.Host == "bailian-singapore-cs.alibabacloud.com" && uri.AbsolutePath == "/data/api.json" ? Cookie : null; }, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(1, calls); Assert.Equal(3, resolutions.Count);
    }
    [Theory]
    [InlineData("login_aliyunid_ticket=other; login_aliyunid_pk=account-a")]
    [InlineData("login_aliyunid_ticket=fixture-ticket; login_aliyunid_pk=account-b")]
    [InlineData("login_aliyunid_ticket=fixture-ticket")]
    public async Task RejectsDifferentAccountAcrossPathsBeforeSending(string dashboard)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(Reply(Quota)); }));
        var reading = await provider.FetchAlibabaCodingWebAsync(null, "intl", uri => uri.Host == "bailian-singapore-cs.alibabacloud.com" ? Cookie : dashboard, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, reading.State); Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData(401, ReadingState.NeedsAuth)]
    [InlineData(403, ReadingState.NeedsAuth)]
    [InlineData(429, ReadingState.Unavailable)]
    public async Task AuthoritativeDiscoveryRefusalStopsTransaction(int status, ReadingState state)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(Reply("{}", (HttpStatusCode)status)); }));
        Assert.Equal(state, (await provider.FetchAlibabaCodingWebAsync(Cookie, "cn", token: TestContext.Current.CancellationToken)).State); Assert.Equal(1, calls);
        if (status == 429) { Assert.Equal(state, (await provider.FetchAlibabaCodingWebAsync(Cookie, "cn", token: TestContext.Current.CancellationToken)).State); Assert.Equal(1, calls); }
    }
    [Theory]
    [InlineData(true, ReadingState.Ready)]
    [InlineData(false, ReadingState.Error)]
    public async Task DiscoveryUnavailableCanUseSameSessionSecurityCookie(bool hasSec, ReadingState state)
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((request, _) =>
        { calls++; return Task.FromResult(request.Method == HttpMethod.Post ? Reply(Quota) : Reply("{}", HttpStatusCode.ServiceUnavailable)); }));
        var reading = await provider.FetchAlibabaCodingWebAsync(hasSec ? Cookie : Cookie.Replace("sec_token=cookie-sec; ", "", StringComparison.Ordinal), "intl", token: TestContext.Current.CancellationToken);
        Assert.Equal(state, reading.State); Assert.Equal(hasSec ? 3 : 2, calls);
    }
    [Theory]
    [InlineData("Cookie: 'login_aliyunid_ticket=t; login_aliyunid_pk=p'")]
    [InlineData("\"Cookie: login_aliyunid_ticket=t; login_aliyunid_pk=p\"")]
    public void NormalizesBalancedManualHeaderQuotes(string value)
    { Assert.Equal("t", AlibabaCodingPlanAuthentication.Cookies(value)["login_aliyunid_ticket"]); }
    [Theory]
    [InlineData("a=1; a=2")]
    [InlineData("a=1\r\nX: y")]
    [InlineData("bad name=1")]
    [InlineData("a=\"quoted\"")]
    public void InvalidCookieHeadersAreRejected(string value) => Assert.Throws<InvalidDataException>(() => AlibabaCodingPlanAuthentication.Cookies(value));
    [Fact]
    public void AmbiguousSecurityMetadataIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => AlibabaCodingPlanAuthentication.HtmlToken("SEC_TOKEN:'a', secToken:'b'"));
        using var json = JsonDocument.Parse("""{"secToken":"a","secToken":"b"}""");
        Assert.Throws<InvalidDataException>(() => AlibabaCodingPlanAuthentication.JsonToken(json.RootElement));
    }
    [Theory]
    [InlineData("-1")]
    [InlineData("0.5")]
    [InlineData("9223372036854775808")]
    [InlineData("true")]
    [InlineData("\"NaN\"")]
    [InlineData("{}")]
    public void MalformedCountsNeverBecomeZero(string value)
        => Assert.Throws<InvalidDataException>(() => Parse("{\"per5HourUsedQuota\":" + value + ",\"per5HourTotalQuota\":100}"));
    [Fact]
    public void DuplicateAndMalformedAliasesCannotHideBehindValidCounter()
    {
        Assert.Throws<InvalidDataException>(() => Parse("""{"per5HourUsedQuota":1,"per5HourUsedQuota":2,"per5HourTotalQuota":100}"""));
        Assert.Throws<InvalidDataException>(() => Parse("""{"per5HourUsedQuota":1,"per5HourTotalQuota":100,"perFiveHourUsedQuota":false}"""));
        Assert.Throws<InvalidDataException>(() => AlibabaCodingPlanUsage.Parse(default));
    }
    [Fact]
    public void ActivePlanWithoutCountersIsCurrentAndDoesNotBorrowOtherSubscription()
    {
        var reading = Parse("""{"codingPlanInstanceInfos":[{"status":"EXPIRED","per5HourUsedQuota":90,"per5HourTotalQuota":100},{"status":"ACTIVE","planName":"Pro"}]}""");
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal("Pro", reading.Plan); Assert.NotNull(reading.UpdatedAt); Assert.Empty(reading.Windows);
    }
    [Fact]
    public void PreservesMissingCountAndNormalizesStaleFiveHourReset()
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(1790053138);
        var reading = Parse("""{"per5HourUsedQuota":10,"per5HourTotalQuota":100,"per5HourQuotaNextRefreshTime":1790053038,"perWeekTotalQuota":100,"perBillMonthUsedQuota":"20","perBillMonthTotalQuota":"200"}""", time);
        Assert.Equal(2, reading.Windows.Count); Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790071038), reading.Headline!.ResetsAt);
        Assert.Equal(43200, reading.Windows[1].DurationMinutes); Assert.Equal("requests", reading.Windows[1].Unit);
    }
    [Fact]
    public void ApiConsoleLoginRequirementIsExplicitAndWebNeedsReconnect()
    {
        const string json = """{"code":"ConsoleNeedLogin"}""";
        Assert.Equal(ReadingState.Unsupported, Parse(json, web: false).State); Assert.Equal(ReadingState.NeedsAuth, Parse(json).State);
    }
    [Theory]
    [InlineData("{\"status\":\"ConsoleNeedLogin\"}")]
    [InlineData("{\"msg\":\"Please log in\"}")]
    [InlineData("{\"code\":\"consoleneedlogin\"}")]
    public void PinnedLoginAliasesRequireTheSelectedWebSession(string json)
    { Assert.Equal(ReadingState.NeedsAuth, Parse(json).State); Assert.Equal(ReadingState.Unsupported, Parse(json, web: false).State); }
    [Theory]
    [InlineData("0.5")]
    [InlineData("\"12:00\"")]
    public void ResetDoesNotInventCurrentDateFromMalformedInput(string reset)
        => Assert.Throws<InvalidDataException>(() => Parse("{\"per5HourUsedQuota\":1,\"per5HourTotalQuota\":100,\"per5HourQuotaNextRefreshTime\":" + reset + "}"));
    [Fact]
    public void AReportedTotalWithoutUsageStaysUnknown()
    { var reading = Parse("{\"per5HourTotalQuota\":100}"); Assert.Equal(ReadingState.Ready, reading.State); Assert.Empty(reading.Windows); }
    [Fact]
    public async Task RegionAndSourceAreCapturedBeforeFirstRequest()
    {
        var region = "intl"; var calls = 0;
        using var provider = new NativeProviders(new Handler((request, _) =>
        { calls++; Assert.EndsWith("alibabacloud.com", request.RequestUri!.Host, StringComparison.Ordinal); region = "cn"; return Task.FromResult(Reply(request.Method == HttpMethod.Get ? "SEC_TOKEN:'a'" : Quota)); }));
        Assert.Equal(ReadingState.Ready, (await provider.FetchAsync("alibaba", Cookie, key => key == "ALIBABA_CODING_PLAN_SOURCE" ? "web" : key == "ALIBABA_CODING_PLAN_REGION" ? region : null, TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, calls);
    }
    [Fact]
    public async Task CancellationStopsAnUncooperativeTransportAndDisposesItsLateResponse()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new NativeProviders(new Handler((_, _) => { entered.SetResult(); return pending.Task; }));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = provider.FetchAlibabaCodingWebAsync(Cookie, "intl", token: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        var content = new DisposalContent(); pending.SetResult(new(HttpStatusCode.OK) { Content = content });
        await content.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }
    [Theory]
    [InlineData("{\"success\":false,\"per5HourUsedQuota\":25,\"per5HourTotalQuota\":100}")]
    [InlineData("{\"per5HourUsedQuota\":25,\"per5HourTotalQuota\":100,\"perFiveHourUsedQuota\":99,\"perFiveHourTotalQuota\":200}")]
    [InlineData("{\"perBillMonthUsedQuota\":25,\"perBillMonthTotalQuota\":100,\"perMonthUsedQuota\":25,\"perMonthTotalQuota\":200}")]
    [InlineData("{\"per5HourUsedQuota\":25,\"per5HourTotalQuota\":100,\"per5HourQuotaNextRefreshTime\":1790071038,\"perFiveHourQuotaNextRefreshTime\":1790072038}")]
    [InlineData("{\"codingPlanInstanceInfos\":[{\"status\":\"ACTIVE\",\"planName\":\"A\",\"per5HourUsedQuota\":25,\"per5HourTotalQuota\":100}],\"coding_plan_instance_infos\":[{\"status\":\"ACTIVE\",\"planName\":\"B\",\"per5HourUsedQuota\":75,\"per5HourTotalQuota\":100}]}")]
    public async Task ExplicitRefusalAndContradictoryAliasesDoNotPublishQuota(string payload)
    {
        Assert.Throws<InvalidDataException>(() => Parse(payload));
        var calls = 0;
        using var provider = new NativeProviders(new Handler((request, _) =>
        { calls++; return Task.FromResult(Reply(request.Method == HttpMethod.Get ? "SEC_TOKEN:'fixture'" : payload)); }));
        var reading = await provider.FetchAlibabaCodingWebAsync(Cookie, "intl", token: TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Empty(reading.Windows); Assert.Equal(2, calls);
    }
    [Fact]
    public void EquivalentAliasesKeepTheirTypedMeaning()
    {
        var reading = Parse("{\"success\":true,\"per5HourUsedQuota\":25,\"per5HourTotalQuota\":100,\"perFiveHourUsedQuota\":\"25\",\"perFiveHourTotalQuota\":\"100\",\"per5HourQuotaNextRefreshTime\":1790071038,\"perFiveHourQuotaNextRefreshTime\":1790071038000}");
        Assert.Equal(25, reading.Headline!.UsedCount);
        const string first = "[{\"status\":\"ACTIVE\",\"planName\":\"A\",\"per5HourUsedQuota\":25,\"per5HourTotalQuota\":100}]";
        const string second = "[{\"per5HourTotalQuota\":\"100\",\"per5HourUsedQuota\":\"25\",\"planName\":\"A\",\"status\":\"ACTIVE\"}]";
        reading = Parse("{\"codingPlanInstanceInfos\":" + first + ",\"coding_plan_instance_infos\":" + JsonSerializer.Serialize(second) + "}");
        Assert.Equal(25, reading.Headline!.UsedCount); Assert.Equal("A", reading.Plan);
    }
    [Theory]
    [InlineData("{\"success\":false,\"code\":\"ConsoleNeedLogin\",\"per5HourUsedQuota\":25,\"per5HourTotalQuota\":100}")]
    [InlineData("{\"success\":false,\"code\":401}")]
    public async Task AuthenticationTakesPriorityOverAnExplicitFailureFlag(string payload)
    {
        Assert.Equal(ReadingState.NeedsAuth, Parse(payload).State);
        using var provider = new NativeProviders(new Handler((request, _) => Task.FromResult(Reply(request.Method == HttpMethod.Get ? "SEC_TOKEN:'fixture'" : payload))));
        Assert.Equal(ReadingState.NeedsAuth, (await provider.FetchAlibabaCodingWebAsync(Cookie, "intl", token: TestContext.Current.CancellationToken)).State);
    }
    private sealed class DisposalContent : StringContent
    {
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DisposalContent() : base("{}") { }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); Disposed.TrySetResult(); }
    }
    [Fact]
    public async Task PreCancellationSendsNothing()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(Reply("{}")); }));
        using var cancellation = new CancellationTokenSource(); await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchAlibabaCodingWebAsync(Cookie, "intl", token: cancellation.Token)); Assert.Equal(0, calls);
    }
    [Fact]
    public async Task OversizedAndInvalidUtf8BodiesAreRejected()
    {
        using var provider = new NativeProviders(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([0xff, 0xfe]) })));
        Assert.Equal(ReadingState.Error, (await provider.FetchAlibabaCodingWebAsync(Cookie, "intl", token: TestContext.Current.CancellationToken)).State);
        using var large = new NativeProviders(new Handler((_, _) => Task.FromResult(Reply(new string('x', 2 * 1024 * 1024 + 1)))));
        Assert.Equal(ReadingState.Error, (await large.FetchAlibabaCodingWebAsync(Cookie, "intl", token: TestContext.Current.CancellationToken)).State);
    }
}
