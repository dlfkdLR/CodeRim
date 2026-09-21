using System.Net;
using System.Text;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
namespace CodeRim.Core.Tests;
public sealed class AmpBrowserTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private const string Fields = "quota:1000,hourlyReplenishment:42,windowHours:24,used:338.5";
    private static string Page(string content) => "<html><script>__sveltekit_x.data={" + content + "};</script></html>";
    private static string Valid => Page("freeTierUsage:{" + Fields + "}");
    [Theory]
    [InlineData("freeTierUsage")]
    [InlineData("getFreeTierUsage")]
    [InlineData("\"w6b2h6/getFreeTierUsage/\"")]
    public void ReadsBothPinnedSvelteShapesWithoutInventingUnits(string key)
    {
        var reading = AmpBrowserUsage.Parse(Page(key + ":{" + Fields + "}"), Now, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(33.85, reading.Headline!.UsedPercent!.Value, 8);
        Assert.Equal(Now.AddHours(338.5 / 42), reading.Headline.ResetsAt);
        Assert.Null(reading.Headline.UsedCount); Assert.Null(reading.Headline.Unit); Assert.Null(reading.CostUsage); Assert.Null(reading.Plan);
    }
    [Fact]
    public void ZeroUsageIsReadyAndNotImmediatelyStale()
    {
        var reading = AmpBrowserUsage.Parse(Page("freeTierUsage:{quota:1000,hourlyReplenishment:42,used:0}"), Now, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.Evaluated(Now).State);
        Assert.Equal(0, reading.Headline!.UsedPercent); Assert.Null(reading.Headline.ResetsAt);
    }
    [Theory]
    [InlineData("1e3", "3.385e2", 33.85)]
    [InlineData("1E+3", "2000", 100)]
    [InlineData("1000.0", "0.0", 0)]
    public void ParsesCompleteFiniteNumericLiterals(string quota, string used, double expected)
    {
        var reading = AmpBrowserUsage.Parse(Page($"freeTierUsage:{{quota:{quota},used:{used},hourlyReplenishment:42}}"), Now, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(expected, reading.Headline!.UsedPercent!.Value, 8);
    }
    [Theory]
    [InlineData("freeTierUsage:null,other:{quota:1000,used:0,hourlyReplenishment:42}")]
    [InlineData("freeTierUsage:{nested:{quota:1000,used:0,hourlyReplenishment:42}}")]
    [InlineData("freeTierUsage:{quota:0,used:0,hourlyReplenishment:42}")]
    [InlineData("freeTierUsage:{quota:-1,used:0,hourlyReplenishment:42}")]
    [InlineData("freeTierUsage:{quota:1000,used:-1,hourlyReplenishment:42}")]
    [InlineData("freeTierUsage:{quota:1000,used:0,hourlyReplenishment:-1}")]
    [InlineData("freeTierUsage:{quota:1000,used:null,hourlyReplenishment:42}")]
    [InlineData("freeTierUsage:{quota:1000,used:'0',hourlyReplenishment:42}")]
    [InlineData("freeTierUsage:{quota:1000,used:0+1,hourlyReplenishment:42}")]
    [InlineData("freeTierUsage:{quota:1e999,used:0,hourlyReplenishment:42}")]
    [InlineData("freeTierUsage:{quota:1000,used:0}")]
    [InlineData("freeTierUsage:{quota:1000,used:0,used:10,hourlyReplenishment:42}")]
    [InlineData("x:'freeTierUsage:{quota:1000,used:0,hourlyReplenishment:42}'")]
    [InlineData("x:/freeTierUsage:{quota:1000,used:0,hourlyReplenishment:42}/")]
    [InlineData("/* freeTierUsage:{quota:1000,used:0,hourlyReplenishment:42} */ x:0")]
    public void NeverConvertsMissingInvalidOrQuotedUsageToAFullAvailableQuota(string script)
    {
        var reading = AmpBrowserUsage.Parse(Page(script), Now, TestContext.Current.CancellationToken);
        Assert.NotEqual(ReadingState.Ready, reading.State); Assert.Empty(reading.Windows);
    }
    [Theory]
    [InlineData("}")]
    [InlineData(",")]
    [InlineData("{")]
    [InlineData("](")]
    public void UnknownStringFieldsAreDataNotObjectDelimiters(string bucket)
    {
        var reading = AmpBrowserUsage.Parse(Page($"freeTierUsage:{{bucket:'{bucket}',{Fields}}}"), Now, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(33.85, reading.Headline!.UsedPercent!.Value, 8);
    }
    [Fact]
    public void ConflictingObjectsAreNotSilentlySelected()
    {
        var reading = AmpBrowserUsage.Parse(Valid + Page("freeTierUsage:{quota:100,used:90,hourlyReplenishment:1}"), Now, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State);
    }
    [Fact]
    public void BoundsHtmlAndDistinguishesSignOutFromUnavailable()
    {
        Assert.Equal(ReadingState.Error, AmpBrowserUsage.Parse(new string('x', 2 * 1024 * 1024 + 1), Now, TestContext.Current.CancellationToken).State);
        Assert.Equal(ReadingState.NeedsAuth, AmpBrowserUsage.Parse("<a href='/login'>Sign in</a>", Now, TestContext.Current.CancellationToken).State);
        Assert.Equal(ReadingState.Unavailable, AmpBrowserUsage.Parse("<html>unknown usage</html>", Now, TestContext.Current.CancellationToken).State);
    }

    [Theory]
    [InlineData("<!-- <script>__sveltekit_x.data={freeTierUsage:{quota:1000,used:0,hourlyReplenishment:42}}</script> --><html>Please sign in</html>")]
    [InlineData("<textarea><script>__sveltekit_x.data={freeTierUsage:{quota:1000,used:0,hourlyReplenishment:42}}</script></textarea>")]
    [InlineData("<script>const p=known?'freeTierUsage':{quota:1000,used:0,hourlyReplenishment:42};</script>")]
    public void DoesNotReadMarkupTextOrTernaryValuesAsUsage(string html)
    {
        var reading = AmpBrowserUsage.Parse(html, Now, TestContext.Current.CancellationToken);
        Assert.NotEqual(ReadingState.Ready, reading.State); Assert.Empty(reading.Windows);
    }
    [Fact]
    public void DivisionBeforeUsageDoesNotConsumeTheRestOfTheScript()
    {
        var reading = AmpBrowserUsage.Parse("<script>const ratio=6 / 2; __sveltekit_x.data={freeTierUsage:{" + Fields + "}};</script>", Now, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State);
    }

    [Theory]
    [InlineData("<template>", "</template>")]
    [InlineData("<noscript>", "</noscript>")]
    [InlineData("<template><template></template>", "</template>")]
    public void InertMarkupCannotSupplyUsage(string prefix, string suffix) =>
        Assert.NotEqual(ReadingState.Ready, AmpBrowserUsage.Parse(prefix + Valid + suffix, Now, TestContext.Current.CancellationToken).State);
    [Theory]
    [InlineData("type='text/plain'")]
    [InlineData("type='application/ld+json'")]
    [InlineData("src='/app.js'")]
    public void NonExecutableScriptBodyCannotSupplyUsage(string attributes) =>
        Assert.NotEqual(ReadingState.Ready, AmpBrowserUsage.Parse(Valid.Replace("<script>", "<script " + attributes + ">", StringComparison.Ordinal), Now, TestContext.Current.CancellationToken).State);
    [Theory]
    [InlineData("function unused(){__sveltekit_x.data={freeTierUsage:{quota:1000,used:0,hourlyReplenishment:42}};}")]
    [InlineData("if(false){__sveltekit_x.data={freeTierUsage:{quota:1000,used:0,hourlyReplenishment:42}};}")]
    [InlineData("__sveltekit_x.data={freeTierUsage:{quota:1000,used:0,hourlyReplenishment:42},freeTierUsage:null};")]
    [InlineData("__sveltekit_x.data={freeTierUsage:{quota:1000,used:0,hourlyReplenishment:42},...{freeTierUsage:null}};")]
    public void AmbiguousOrUnevaluatedHydrationDoesNotPublishAQuota(string script) =>
        Assert.NotEqual(ReadingState.Ready, AmpBrowserUsage.Parse("<script>" + script + "</script>", Now, TestContext.Current.CancellationToken).State);
    [Fact]
    public void RepeatedRawTextDoesNotAllocateQuadraticCopies()
    {
        var html = string.Concat(Enumerable.Repeat("<textarea></textarea>", 5000)) + new string(' ', 1500000);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.NotEqual(ReadingState.Ready, AmpBrowserUsage.Parse(html, Now, TestContext.Current.CancellationToken).State);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024 * 1024, "Raw-text scanning allocated more than 64 MiB");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => run(request, token); }
    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent(Valid, Encoding.UTF8, "text/html") };
    [Theory]
    [InlineData("fixture-session")]
    [InlineData("session=fixture-session; unrelated=do-not-send")]
    [InlineData("Cookie: 'session=fixture-session'")]
    public async Task WebSendsOnlyTheSessionToTheFixedSettingsGet(string credential)
    {
        var calls = 0; using var native = new NativeProviders(new Handler((request, _) =>
        {
            calls++; Assert.Equal("https://ampcode.com/settings", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content); Assert.Null(request.Headers.Authorization);
            Assert.Equal("session=fixture-session", Assert.Single(request.Headers.GetValues("Cookie"))); return Task.FromResult(Ok());
        }));
        var reading = await native.FetchAsync("amp", credential, key => key == "AMP_USAGE_SOURCE" ? "web" : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("other=value")]
    [InlineData("session=first; session=second")]
    [InlineData("session=value\r\nX-Injection: y")]
    public async Task BadOrUnscopedSessionDoesNotMakeAnyRequest(string? cookie)
    {
        var calls = 0; using var native = new NativeProviders(new Handler((_, _) => { calls++; return Task.FromResult(Ok()); }));
        var reading = await native.FetchAmpBrowserAsync("fixture-fallback", _ => cookie, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, reading.State); Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData(302, ReadingState.Error)]
    [InlineData(401, ReadingState.NeedsAuth)]
    [InlineData(403, ReadingState.NeedsAuth)]
    [InlineData(429, ReadingState.Unavailable)]
    [InlineData(503, ReadingState.Error)]
    public async Task MapsFailuresWithoutFallbackOrResponseLeak(int status, ReadingState expected)
    {
        var calls = 0; using var native = new NativeProviders(new Handler((_, _) =>
        { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private-upstream-body") }); }));
        var reading = await native.FetchAmpBrowserAsync("session=fixture", token: TestContext.Current.CancellationToken);
        Assert.Equal(expected, reading.State); Assert.DoesNotContain("private-upstream-body", reading.Message); Assert.Equal(1, calls);
        if (status == 429) { await native.FetchAmpBrowserAsync("session=fixture", token: TestContext.Current.CancellationToken); Assert.Equal(1, calls); }
    }
    [Theory]
    [InlineData("/settings/usage", ReadingState.Ready, 2)]
    [InlineData("/login", ReadingState.NeedsAuth, 1)]
    [InlineData("/signin", ReadingState.NeedsAuth, 1)]
    [InlineData("/sign-in", ReadingState.NeedsAuth, 1)]
    [InlineData("https://auth.ampcode.com/?client_id=fixture", ReadingState.NeedsAuth, 1)]
    [InlineData("https://auth.ampcode.com/login", ReadingState.NeedsAuth, 1)]
    [InlineData("https://other.invalid/settings", ReadingState.Error, 1)]
    [InlineData("http://ampcode.com/settings", ReadingState.Error, 1)]
    [InlineData("https://ampcode.com:8443/settings", ReadingState.Error, 1)]
    [InlineData("/other", ReadingState.Error, 1)]
    public async Task RedirectsAreBoundedAndReapplyUriScopedCookies(string location, ReadingState state, int expectedCalls)
    {
        var calls = 0;
        using var native = new NativeProviders(new Handler((request, _) =>
        {
            calls++;
            if (calls > 1) { Assert.Equal("session=path-specific", Assert.Single(request.Headers.GetValues("Cookie"))); return Task.FromResult(Ok()); }
            var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new(location, UriKind.RelativeOrAbsolute);
            return Task.FromResult(response);
        }));
        var reading = await native.FetchAmpBrowserAsync(null, uri => uri.AbsolutePath == "/settings" ? "session=initial" : "session=path-specific", TestContext.Current.CancellationToken);
        Assert.Equal(state, reading.State); Assert.Equal(expectedCalls, calls);
    }
    [Fact]
    public async Task RepeatedRedirectStopsWithoutLooping()
    {
        var calls = 0; using var native = new NativeProviders(new Handler((_, _) =>
        {
            calls++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("/settings", UriKind.Relative);
            return Task.FromResult(response);
        }));
        Assert.Equal(ReadingState.Error, (await native.FetchAmpBrowserAsync("session=fixture", token: TestContext.Current.CancellationToken)).State);
        Assert.Equal(1, calls);
    }
    [Fact]
    public void ObjectExpressionsAndNestedTemplatesAreNotQuotaValues()
    {
        Assert.NotEqual(ReadingState.Ready, AmpBrowserUsage.Parse(Page("freeTierUsage:{quota:1000,used:0,hourlyReplenishment:42} && null"), Now, TestContext.Current.CancellationToken).State);
        var tick = ((char)96).ToString();
        var page = "<script>const t=" + tick + "" + tick + " freeTierUsage:{quota:1000,used:0,hourlyReplenishment:42} " + tick + " hello" + tick + ";</script>";
        Assert.NotEqual(ReadingState.Ready, AmpBrowserUsage.Parse(page, Now, TestContext.Current.CancellationToken).State);
    }
    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var native = new NativeProviders(new Handler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Ok(); }));
        using var cancel = new CancellationTokenSource(); cancel.CancelAfter(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => native.FetchAmpBrowserAsync("session=fixture", token: cancel.Token));
    }
    [Fact]
    public async Task RejectsOversizedAndInvalidUtf8Bodies()
    {
        foreach (var bytes in new[] { new byte[2 * 1024 * 1024 + 1], new byte[] { 0xff, 0xfe, 0xff } })
        {
            using var native = new NativeProviders(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
            var reading = await native.FetchAmpBrowserAsync("session=fixture", token: TestContext.Current.CancellationToken);
            Assert.Equal(ReadingState.Error, reading.State);
        }
    }
}
