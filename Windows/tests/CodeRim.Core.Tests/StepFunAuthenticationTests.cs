using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class StepFunAuthenticationTests
{
    private const string Usage = """{"status":1,"five_hour_usage_left_rate":0.67,"weekly_usage_left_rate":0.5,"five_hour_usage_reset_time":1801000000,"weekly_usage_reset_time":1801000000}""";
    private const string Plan = """{"status":1,"subscription":{"name":"Step Plan"}}""";
    private static readonly string[] LoginPaths = ["/", "/passport/proto.api.passport.v1.PassportService/RegisterDevice",
        "/passport/proto.api.passport.v1.PassportService/SignInByPassword", "/api/step.openapi.devcenter.Dashboard/QueryStepPlanRateLimit", "/api/step.openapi.devcenter.Dashboard/GetStepPlanStatus"];
    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    [Theory]
    [InlineData(" access...refresh ", "access...refresh")]
    [InlineData("Cookie: Other=one; Oasis-Token=access...refresh", "access...refresh")]
    [InlineData("\"access...refresh\"", "access...refresh")]
    [InlineData("Oasis-Token=a; Oasis-Token=b", null)]
    [InlineData("unsafe\nheader", null)]
    [InlineData("Other=one; Wrong=two", null)]
    public void TokensNormalizeOnlyOneSafeValue(string raw, string? expected) => Assert.Equal(expected, StepFunAuthentication.Token(raw));
    [Theory]
    [InlineData("""{"accessToken":{"raw":"a"},"refreshToken":{"raw":"r"}}""", "a...r")]
    [InlineData("""{"accessToken":{"raw":"a"}}""", "a")]
    [InlineData("""{"accessToken":{"raw":"a"},"refreshToken":null}""", "a")]
    public void PassportTokensUseThreeDotSeparator(string json, string expected)
    { using var document = JsonDocument.Parse(json); Assert.Equal(expected, StepFunAuthentication.CombinedToken(document.RootElement)); }
    [Theory]
    [InlineData("""{"accessToken":123}""")]
    [InlineData("""{"accessToken":{"raw":"a"},"refreshToken":{"raw":123}}""")]
    [InlineData("""{"accessToken":{"raw":"a","raw":"b"}}""")]
    [InlineData("""{"accessToken":{"raw":"a"},"accessToken":{"raw":"b"}}""")]
    [InlineData("""{"accessToken":{"raw":"bad\nvalue"}}""")]
    public void MalformedPassportTokensAreRejected(string json)
    { using var document = JsonDocument.Parse(json); Assert.Throws<InvalidDataException>(() => StepFunAuthentication.CombinedToken(document.RootElement)); }
    [Fact]
    public void RefreshEnvelopeKeepsOwnerScopeWhileNewInputChangesIt()
    {
        var original = StepFunAuthentication.Manual("old")!;
        var rotated = StepFunAuthentication.Saved(JsonSerializer.Serialize(original with { Token = "new" }))!;
        Assert.Equal(StepFunAuthentication.Scope(original), StepFunAuthentication.Scope(rotated));
        Assert.NotEqual(StepFunAuthentication.Scope(original), StepFunAuthentication.Scope(StepFunAuthentication.Manual("new")!));
        Assert.Null(StepFunAuthentication.Saved("""{"Owner":"short","Kind":"manual","Token":"x"}"""));
        Assert.Null(StepFunAuthentication.Saved("""{"Owner":"x","Owner":"y"}"""));
    }
    [Fact]
    public async Task PasswordLoginUsesIngressRegistrationAndFixedOriginWithExactPassword()
    {
        var paths = new List<string>();
        using var provider = new NativeProviders(new Handler(async (request, token) =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme); Assert.Equal("platform.stepfun.com", request.RequestUri.Host);
            paths.Add(request.RequestUri.AbsolutePath);
            Assert.Equal("10300", request.Headers.GetValues("oasis-appid").Single());
            var path = request.RequestUri.AbsolutePath;
            if (path == "/") { var response = Ok(""); response.Headers.Add("Set-Cookie", "INGRESSCOOKIE=fixture; Secure; Path=/"); return response; }
            Assert.Equal(HttpMethod.Post, request.Method);
            if (path.EndsWith("RegisterDevice", StringComparison.Ordinal))
            { Assert.Equal("INGRESSCOOKIE=fixture", request.Headers.GetValues("Cookie").Single()); return Ok("""{"accessToken":{"raw":"anonymous"}}"""); }
            if (path.EndsWith("SignInByPassword", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("test-user", body.RootElement.GetProperty("username").GetString()); Assert.Equal(" pass word ", body.RootElement.GetProperty("password").GetString());
                Assert.Contains("Oasis-Token=anonymous", request.Headers.GetValues("Cookie").Single());
                return Ok("""{"accessToken":{"raw":"logged-in"},"refreshToken":{"raw":"refresh"}}""");
            }
            Assert.Contains("Oasis-Token=logged-in...refresh", request.Headers.GetValues("Cookie").Single());
            return Ok(path.EndsWith("GetStepPlanStatus", StringComparison.Ordinal) ? Plan : Usage);
        }));
        var result = await provider.FetchStepFunAsync(StepFunAuthentication.Login("test-user", " pass word ")!, token: TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, result.Reading.State); Assert.Equal(33, result.Reading.Headline!.UsedPercent!.Value, 5);
        Assert.Equal("Step Plan", result.Reading.Plan); Assert.Equal("logged-in...refresh", result.Token); Assert.Equal(LoginPaths, paths);
    }
    [Fact]
    public async Task GenericProviderEntryPreservesPasswordWhitespace()
    {
        var login = false;
        using var provider = new NativeProviders(new Handler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/") { var response = Ok(""); response.Headers.Add("Set-Cookie", "INGRESSCOOKIE=fixture"); return response; }
            if (path.EndsWith("RegisterDevice", StringComparison.Ordinal)) return Ok("""{"accessToken":{"raw":"anonymous"}}""");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(" pass word ", body.RootElement.GetProperty("password").GetString()); login = true;
            return new(HttpStatusCode.Unauthorized);
        }));
        var reading = await provider.FetchAsync("stepfun", null, key => key == "STEPFUN_USERNAME" ? "user" : key == "STEPFUN_PASSWORD" ? " pass word " : null, TestContext.Current.CancellationToken);
        Assert.True(login); Assert.Equal(ReadingState.NeedsAuth, reading.State);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredTokenRefreshesOnceBeforeRetry(bool payloadFailure)
    {
        var quota = 0; var refresh = 0;
        using var provider = new NativeProviders(new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("RefreshToken", StringComparison.Ordinal)) { refresh++; Assert.Equal("old", request.Headers.GetValues("Oasis-Token").Single()); return Task.FromResult(Ok("""{"accessToken":{"raw":"new"}}""")); }
            if (path.EndsWith("QueryStepPlanRateLimit", StringComparison.Ordinal) && quota++ == 0)
                return Task.FromResult(payloadFailure ? Ok("""{"status":0,"message":"token expired"}""") : new(HttpStatusCode.Unauthorized));
            return Task.FromResult(Ok(path.EndsWith("GetStepPlanStatus", StringComparison.Ordinal) ? Plan : Usage));
        }));
        var result = await provider.FetchStepFunAsync(StepFunAuthentication.Manual("old")!, token: TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, result.Reading.State); Assert.Equal("new", result.Token); Assert.Equal(1, refresh); Assert.Equal(2, quota);
    }
    [Fact]
    public async Task ManualFailureCannotUsePasswordOrLoop()
    {
        var paths = new List<string>();
        using var provider = new NativeProviders(new Handler((request, _) => { paths.Add(request.RequestUri!.AbsolutePath); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }));
        var result = await provider.FetchStepFunAsync(new(StepFunAuthentication.Hash("fixture"), "manual", "old", "unused", "unused"), token: TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, result.Reading.State); Assert.Equal(2, paths.Count);
        Assert.DoesNotContain(paths, path => path.EndsWith("SignInByPassword", StringComparison.Ordinal));
    }
    [Fact]
    public async Task Optional429KeepsQuotaAndCannotThrottleRequiredRefresh()
    {
        var required = 0;
        using var provider = new NativeProviders(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("GetStepPlanStatus", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            required++; return Task.FromResult(Ok(Usage));
        }));
        for (var i = 0; i < 2; i++) Assert.Equal(ReadingState.Partial, (await provider.FetchStepFunAsync(StepFunAuthentication.Manual("one")!, token: TestContext.Current.CancellationToken)).Reading.State);
        Assert.Equal(2, required);
    }
    [Fact]
    public async Task Required429UsesStableOwnerButDoesNotBlockReplacementAccount()
    {
        var count = 0;
        using var provider = new NativeProviders(new Handler((_, _) => { count++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)); }));
        var first = StepFunAuthentication.Manual("first")!;
        await provider.FetchStepFunAsync(first, token: TestContext.Current.CancellationToken);
        await provider.FetchStepFunAsync(first with { Token = "rotated" }, token: TestContext.Current.CancellationToken);
        Assert.Equal(1, count);
        await provider.FetchStepFunAsync(StepFunAuthentication.Manual("second")!, token: TestContext.Current.CancellationToken); Assert.Equal(2, count);
    }
    [Fact]
    public async Task CallerCancellationStopsUncooperativeTransport()
    {
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource(); using var provider = new NativeProviders(new Handler((_, _) => held.Task));
        var task = provider.FetchStepFunAsync(StepFunAuthentication.Manual("fixture")!, token: cancel.Token); cancel.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)); }
        finally { held.TrySetResult(Ok(Usage)); }
    }
    [Fact]
    public async Task BrowserPathScopeCannotEnrichWithAnotherSession()
    {
        var count = 0; using var provider = new NativeProviders(new Handler((_, _) => { count++; return Task.FromResult(Ok(Usage)); }));
        var result = await provider.FetchStepFunAsync(StepFunAuthentication.Manual("selected")!,
            uri => uri == NativeProviders.StepFunUsageUri ? "Oasis-Token=selected" : "Oasis-Token=other", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Partial, result.Reading.State); Assert.Equal(1, count);
    }
}
