using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

/// <summary>
/// The Windows counterpart of the macOS ladder: connecting a provider from plain routing up to
/// races, hostile input and misbehaving networks. Most of it sits at the hard end.
/// </summary>
public sealed class ProviderConnectionLadderTests : IDisposable
{
    private readonly Func<GoogleOAuthClient?> savedLocator = ProviderSignIn.AntigravityClient;
    public void Dispose() => ProviderSignIn.AntigravityClient = savedLocator;

    private static SignInPlan Web(string id = "p") => new(SignInKind.Browser, "Tool", null, new Uri("https://example.com"), "Sign in.");
    private static SignInPlan InApp(InAppKind kind = InAppKind.GitHubDevice) => new(SignInKind.InApp, "GitHub", null, null, "n") { InApp = kind };

    private static ProviderConnector Make(SignInPlan plan, Func<string, Task<bool>>? connects = null, Func<SignInPlan, bool>? launch = null,
        Func<SignInPlan, string?>? preflight = null, Action<SignInPlan>? install = null,
        Func<InAppKind, Action<string>, CancellationToken, Task<InAppOutcome>>? runInApp = null, Func<string, string?>? blocker = null, int patienceMs = 5000)
        => new(connects ?? (_ => Task.FromResult(false)), launch ?? (_ => true), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(patienceMs),
            plan: _ => plan, preflight: preflight ?? (_ => null), openInstallPage: install, runInApp: runInApp, blocker: blocker);

    private static async Task Until(Func<bool> done, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end && !done()) await Task.Delay(5, TestContext.Current.CancellationToken);
    }

    // Level 1 — routing

    [Fact] public void L01CopilotSignsInInsideTheApp() { var p = ProviderSignIn.For("copilot"); Assert.Equal(SignInKind.InApp, p.Kind); Assert.Equal(InAppKind.GitHubDevice, p.InApp); }

    [Fact] public void L02AntigravityUsesGoogleWhenItsAppIsInstalled()
    {
        ProviderSignIn.AntigravityClient = () => new("1-a.apps.googleusercontent.com", "GOCSPX-" + new string('x', 28));
        Assert.Equal(InAppKind.AntigravityGoogle, ProviderSignIn.For("gemini").InApp);
    }

    [Fact] public void L03AntigravityWithoutItsAppOpensTheDownloadPage()
    {
        ProviderSignIn.AntigravityClient = () => null;
        var plan = ProviderSignIn.For("gemini");
        Assert.Equal(SignInKind.Browser, plan.Kind); Assert.Contains("download", plan.Url!.AbsoluteUri);
    }

    [Theory, InlineData("glm"), InlineData("ollama")]
    public void L04KeyProvidersAskForAKeyInCodeRim(string id) { var p = ProviderSignIn.For(id); Assert.True(p.OpensSettings); Assert.Contains("Paste", p.Note); }

    [Fact] public void L05GeminiCliTellsPersonalAccountsToUseAntigravity() => Assert.Contains("Antigravity", ProviderSignIn.For("gemini-cli").Note);

    [Fact] public void L06TerminalSignInsKnowWhereToInstallTheirTool() => Assert.NotNull(ProviderSignIn.For("cursor").InstallUrl);

    [Fact] public void L07WebsiteSignInsSayHowTheSessionIsImported()
    {
        var plan = ProviderSignIn.For("perplexity");
        Assert.Equal(SignInKind.Browser, plan.Kind); Assert.Contains("Import from", plan.Note);
    }

    [Fact] public void L08AnInstalledToolPassesPreflight() => Assert.Null(ProviderSignIn.MissingTool(new(SignInKind.Terminal, "T", "tool login", null, "n"), _ => true));

    // Level 2 — connector states

    [Fact] public async Task L09AnExistingSignInConnectsWithoutOpeningAnything()
    {
        var launched = 0; var c = Make(Web(), _ => Task.FromResult(true), _ => { launched++; return true; });
        await c.RunAsync("p");
        Assert.Equal(ProviderConnector.Phase.Connected, c.StateOf("p")!.Phase); Assert.Equal(0, launched);
    }

    [Fact] public async Task L10AKeyRouteWaitsForTheKey()
    {
        var c = Make(new(SignInKind.Settings, "Z", null, null, "Paste it."));
        await c.RunAsync("p");
        Assert.Equal(new ProviderConnector.State(ProviderConnector.Phase.NeedsKey, "Paste it."), c.StateOf("p"));
    }

    [Fact] public async Task L11ASignInThatCannotOpenSaysSo()
    {
        var c = Make(Web(), launch: _ => false);
        await c.RunAsync("p");
        Assert.Contains("could not be opened", c.StateOf("p")!.Message);
    }

    [Fact] public async Task L12PatienceRunsOutWithAPlainMessage()
    {
        var c = Make(Web(), patienceMs: 40);
        await c.RunAsync("p");
        Assert.Contains("not detected", c.StateOf("p")!.Message);
    }

    [Fact] public async Task L13TheSignInIsWatchedUntilTheAccountAppears()
    {
        var checks = 0; var c = Make(Web(), _ => Task.FromResult(++checks >= 4));
        await c.RunAsync("p");
        Assert.Equal(ProviderConnector.Phase.Connected, c.StateOf("p")!.Phase);
    }

    [Fact] public async Task L13bAReadThatThrowsEndsTheSignInWithAReason()
    {
        var checks = 0;
        var c = Make(Web(), _ => ++checks >= 2 ? throw new InvalidOperationException("store offline") : Task.FromResult(false));
        await c.RunAsync("p");
        Assert.Equal(ProviderConnector.Phase.Failed, c.StateOf("p")!.Phase);
        Assert.Contains("store offline", c.StateOf("p")!.Message);
    }

    [Fact] public async Task L13cAnInAppSignInThatThrowsIsNotLeftWaiting()
    {
        var c = Make(new(SignInKind.InApp, "G", null, null, "n") { InApp = InAppKind.GitHubDevice },
            runInApp: (_, _, _) => throw new HttpRequestException("no network"));
        await c.RunAsync("p");
        Assert.Equal(ProviderConnector.Phase.Failed, c.StateOf("p")!.Phase);
    }

    [Fact] public async Task L14AMissingToolOpensItsInstallPageInsteadOfAnEmptyTerminal()
    {
        var pages = 0; var launched = 0;
        var plan = new SignInPlan(SignInKind.Terminal, "T", "definitely-not-installed-xyz login", null, "n") { InstallUrl = new Uri("https://example.com/i") };
        var c = Make(plan, launch: _ => { launched++; return true; }, preflight: p => ProviderSignIn.MissingTool(p), install: _ => pages++);
        await c.RunAsync("p");
        Assert.Contains("definitely-not-installed-xyz", c.StateOf("p")!.Message); Assert.Equal(1, pages); Assert.Equal(0, launched);
    }

    [Fact] public async Task L15ABlockerEndsTheWaitWithItsReason()
    {
        var c = Make(Web(), blocker: _ => "Plan does not include usage.");
        await c.RunAsync("p");
        Assert.Equal(new ProviderConnector.State(ProviderConnector.Phase.Failed, "Plan does not include usage."), c.StateOf("p"));
    }

    [Fact] public async Task L16ALateBlockerLetsTheWaitRunUntilItAppears()
    {
        var polls = 0; var c = Make(Web(), _ => { polls++; return Task.FromResult(false); }, blocker: _ => polls >= 6 ? "Refused." : null);
        await c.RunAsync("p");
        Assert.True(polls >= 6); Assert.Equal("Refused.", c.StateOf("p")!.Message);
    }

    [Fact] public async Task L17AnInAppSignInConnectsWhenItFinishes()
    {
        var c = Make(InApp(), _ => Task.FromResult(false), runInApp: (_, _, _) => Task.FromResult(InAppOutcome.Success));
        var checks = 0; c = Make(InApp(), _ => Task.FromResult(++checks >= 2), runInApp: (_, _, _) => Task.FromResult(InAppOutcome.Success));
        await c.RunAsync("p");
        Assert.Equal(ProviderConnector.Phase.Connected, c.StateOf("p")!.Phase);
    }

    [Fact] public async Task L18AFailedInAppSignInSaysWhy()
    {
        var c = Make(InApp(), runInApp: (_, _, _) => Task.FromResult(InAppOutcome.Failed("Install the Antigravity app first")));
        await c.RunAsync("p");
        Assert.Equal("Install the Antigravity app first", c.StateOf("p")!.Message);
    }

    // Level 3 — races

    [Fact] public async Task L19CancellingWhileCheckingNeverLeavesAConnectedState()
    {
        var gate = new TaskCompletionSource<bool>();
        var c = Make(Web(), _ => gate.Task);
        var run = c.RunAsync("p");
        await Task.Delay(20, TestContext.Current.CancellationToken); c.Cancel("p"); gate.SetResult(true); await run;
        Assert.Null(c.StateOf("p"));
    }

    [Fact] public async Task L20CancellingWhileWaitingNeverLeavesAConnectedState()
    {
        var checks = 0; var gate = new TaskCompletionSource<bool>();
        var c = Make(Web(), _ => ++checks < 3 ? Task.FromResult(false) : gate.Task);
        var run = c.RunAsync("p");
        await Until(() => checks >= 3); c.Cancel("p"); gate.SetResult(true); await run;
        Assert.Null(c.StateOf("p"));
    }

    [Fact] public async Task L21StartingTwiceOpensTheSignInOnce()
    {
        var launched = 0; var gate = new TaskCompletionSource<bool>();
        var c = Make(Web(), _ => gate.Task, _ => { Interlocked.Increment(ref launched); return true; });
        var first = c.RunAsync("p"); var second = c.RunAsync("p");
        gate.SetResult(false);
        await Until(() => c.StateOf("p")?.Phase == ProviderConnector.Phase.Waiting); await Task.Delay(30, TestContext.Current.CancellationToken);
        c.Cancel("p"); await Task.WhenAll(first, second);
        Assert.Equal(1, launched);
    }

    [Fact] public async Task L22AnOlderRunCannotOverwriteANewerOne()
    {
        var first = true; var slow = new TaskCompletionSource<bool>();
        var c = Make(new(SignInKind.Settings, "Z", null, null, "new"), id => { if (first) { first = false; return slow.Task; } return Task.FromResult(false); });
        var old = c.RunAsync("p"); await Task.Delay(10, TestContext.Current.CancellationToken);
        await c.RunAsync("p");
        slow.SetResult(true); await old;
        Assert.Equal(ProviderConnector.Phase.NeedsKey, c.StateOf("p")!.Phase);
    }

    [Fact] public async Task L23InAppStepsAreShownWhileTheyHappen()
    {
        var release = new TaskCompletionSource<InAppOutcome>();
        var c = Make(InApp(), runInApp: (_, update, _) => { update("Enter the code ZZZZ-9999"); return release.Task; });
        var run = c.RunAsync("p");
        await Until(() => c.StateOf("p")?.Message == "Enter the code ZZZZ-9999");
        Assert.Equal("Enter the code ZZZZ-9999", c.StateOf("p")!.Message);
        c.Cancel("p"); release.SetResult(InAppOutcome.Success); await run;
    }

    [Fact] public async Task L24AStepReportedAfterCancelIsIgnored()
    {
        Action<string>? late = null; var release = new TaskCompletionSource<InAppOutcome>();
        var c = Make(InApp(), runInApp: (_, update, _) => { late = update; return release.Task; });
        var run = c.RunAsync("p");
        await Until(() => late is not null); c.Cancel("p");
        late!("late step"); release.SetResult(InAppOutcome.Success); await run;
        Assert.Null(c.StateOf("p"));
    }

    [Fact] public async Task L25TryingAgainAfterAFailureStartsFresh()
    {
        var plan = new SignInPlan(SignInKind.Browser, "T", null, new Uri("https://example.com"), "n");
        var launch = false;
        var c = new ProviderConnector(_ => Task.FromResult(false), _ => launch, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(30), plan: _ => plan, preflight: _ => null);
        await c.RunAsync("p"); Assert.Equal(ProviderConnector.Phase.Failed, c.StateOf("p")!.Phase);
        launch = true; var again = c.RunAsync("p");
        await Until(() => c.StateOf("p")?.Phase == ProviderConnector.Phase.Waiting);
        Assert.Equal(ProviderConnector.Phase.Waiting, c.StateOf("p")!.Phase);
        await again;
    }

    // Level 3 — GitHub device flow against a scripted GitHub

    private sealed class Script(params (HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
    {
        private int next;
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath + " " + (request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            var (status, body) = replies[Math.Min(next++, replies.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
    private const string Code = """{"device_code":"dev","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":1}""";
    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    private static async Task<(InAppOutcome Outcome, string? Saved, List<string> Shown, Script Http, Uri? Opened, string? Copied)> Device(params (HttpStatusCode, string)[] replies)
    {
        var script = new Script(replies); string? saved = null, copied = null; Uri? opened = null; var shown = new List<string>();
        var outcome = await GitHubDeviceFlow.RunAsync(new HttpClient(script), shown.Add, u => opened = u, c => copied = c, t => saved = t, CancellationToken.None, NoDelay);
        return (outcome, saved, shown, script, opened, copied);
    }

    [Fact] public async Task L26TheDeviceCodeIsShownCopiedAndApproved()
    {
        var r = await Device((HttpStatusCode.OK, Code), (HttpStatusCode.OK, """{"error":"authorization_pending"}"""), (HttpStatusCode.OK, """{"access_token":"gho_x"}"""));
        Assert.True(r.Outcome.SignedIn); Assert.Equal("gho_x", r.Saved); Assert.Equal("ABCD-1234", r.Copied);
        Assert.Contains("ABCD-1234", r.Shown[0]); Assert.Equal("github.com", r.Opened!.Host);
    }

    [Fact] public async Task L27SlowDownIsHonoured()
    {
        var r = await Device((HttpStatusCode.OK, Code), (HttpStatusCode.OK, """{"error":"slow_down"}"""), (HttpStatusCode.OK, """{"access_token":"t"}"""));
        Assert.True(r.Outcome.SignedIn); Assert.Equal(3, r.Http.Requests.Count);
    }

    [Fact] public async Task L28AnExpiredCodeAsksForANewOne()
    {
        var r = await Device((HttpStatusCode.OK, Code), (HttpStatusCode.OK, """{"error":"expired_token"}"""));
        Assert.False(r.Outcome.SignedIn); Assert.Contains("expired", r.Outcome.Reason); Assert.Null(r.Saved);
    }

    [Fact] public async Task L29ADeclinedCodeIsReported()
    {
        var r = await Device((HttpStatusCode.OK, Code), (HttpStatusCode.OK, """{"error":"access_denied"}"""));
        Assert.Contains("declined", r.Outcome.Reason);
    }

    [Fact] public async Task L30GitHubRefusingToIssueACodeFails()
    {
        var r = await Device((HttpStatusCode.ServiceUnavailable, "{}"));
        Assert.False(r.Outcome.SignedIn); Assert.Contains("GitHub", r.Outcome.Reason);
    }

    [Fact] public async Task L31AnIncompleteOrHostileCodeIsRejected()
    {
        var r = await Device((HttpStatusCode.OK, """{"device_code":"d","user_code":"X","verification_uri":"http://evil.example/","interval":1}"""));
        Assert.False(r.Outcome.SignedIn); Assert.Null(r.Opened);
    }

    [Fact] public async Task L32GarbledJsonIsAReasonNotACrash()
    {
        var r = await Device((HttpStatusCode.OK, "not json"));
        Assert.False(r.Outcome.SignedIn);
    }

    [Fact] public async Task L33ATokenThatCannotBeSavedIsNotASuccess()
    {
        var script = new Script((HttpStatusCode.OK, Code), (HttpStatusCode.OK, """{"access_token":"t"}"""));
        var outcome = await GitHubDeviceFlow.RunAsync(new HttpClient(script), _ => { }, _ => { }, _ => { }, _ => throw new IOException("disk"), CancellationToken.None, NoDelay);
        Assert.False(outcome.SignedIn);
    }

    [Fact] public async Task L34CancellingTheDeviceFlowSaysCancelled()
    {
        using var cancel = new CancellationTokenSource();
        var script = new Script((HttpStatusCode.OK, Code), (HttpStatusCode.OK, """{"error":"authorization_pending"}"""));
        var run = GitHubDeviceFlow.RunAsync(new HttpClient(script), _ => { }, _ => { }, _ => { }, _ => { }, cancel.Token,
            async (_, token) => await Task.Delay(Timeout.Infinite, token));
        cancel.Cancel();
        Assert.Equal("Sign-in was cancelled.", (await run).Reason);
    }

    // Level 3 — Antigravity's OAuth client and the loopback redirect

    [Fact] public void L35TheClientIsFoundNextToItsMarker()
    {
        var text = "junk 999-wrong.apps.googleusercontent.com GOCSPX-" + new string('w', 28) + " vs/platform/cloudCode/common/oauthClient.js x=\"123-abc.apps.googleusercontent.com\",y=\"GOCSPX-" + new string('r', 28) + "\"";
        var client = AntigravityOAuthClientLocator.Parse(text);
        Assert.Equal("123-abc.apps.googleusercontent.com", client!.ClientId); Assert.EndsWith("rrrr", client.ClientSecret);
    }

    [Fact] public void L36NoClientInTheAppMeansNoGoogleSignIn()
    {
        Assert.Null(AntigravityOAuthClientLocator.Parse("vs/platform/cloudCode/common/oauthClient.js nothing here"));
        Assert.Null(AntigravityOAuthClientLocator.Discover(["/nope/main.js"], _ => null));
    }

    [Fact] public void L37CandidatePathsCoverPerUserAndMachineInstalls()
    {
        var paths = AntigravityOAuthClientLocator.CandidatePaths(k => k switch { "LOCALAPPDATA" => "L", "ProgramFiles" => "P", _ => null }).ToList();
        Assert.Contains(paths, p => p.StartsWith(Path.Combine("L", "Programs", "Antigravity"), StringComparison.Ordinal));
        Assert.Contains(paths, p => p.StartsWith(Path.Combine("P", "Antigravity"), StringComparison.Ordinal));
    }

    [Fact] public async Task L38AntigravityWithoutItsAppAsksForTheInstall()
    {
        var outcome = await AntigravityGoogleSignIn.RunAsync(null, new HttpClient(), _ => { }, _ => true, _ => { }, CancellationToken.None);
        Assert.Contains("Install the Antigravity app", outcome.Reason);
    }

    private static async Task<int> Status(Uri url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await http.GetAsync(url);
        return (int)response.StatusCode;
    }

    private static string? Raw(int port, IEnumerable<byte[]> chunks, int gapMs = 0, int readTimeoutMs = 3000)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveTimeout = readTimeoutMs, SendTimeout = readTimeoutMs };
        socket.Connect(IPAddress.Loopback, port);
        try { foreach (var chunk in chunks) { socket.Send(chunk); if (gapMs > 0) Thread.Sleep(gapMs); } }
        catch (SocketException) { }
        var buffer = new byte[4096];
        try { var n = socket.Receive(buffer); return Encoding.ASCII.GetString(buffer, 0, n); } catch (SocketException) { return null; }
    }

    [Fact] public async Task L39TheRedirectIgnoresTheFaviconAndSettlesOnTheCallback()
    {
        using var server = new OAuthLoopbackServer("s"); var url = server.Start();
        Assert.Equal(404, await Status(new Uri(url, "/favicon.ico")));
        Assert.Equal(200, await Status(new Uri(url.AbsoluteUri + "?code=C&state=s")));
        Assert.Equal("C", (await server.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None)).Code);
    }

    [Fact] public async Task L40AForgedStateIsRefused()
    {
        using var server = new OAuthLoopbackServer("real"); var url = server.Start();
        Assert.Equal(400, await Status(new Uri(url.AbsoluteUri + "?code=C&state=fake")));
        Assert.NotNull((await server.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None)).Error);
    }

    [Fact] public async Task L41ACallbackWithoutStateIsNotASuccess()
    {
        using var server = new OAuthLoopbackServer("s"); var url = server.Start();
        Assert.Equal(400, await Status(new Uri(url.AbsoluteUri + "?code=C")));
        Assert.Equal("missing state", (await server.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None)).Error);
    }

    [Fact] public async Task L42ADeclinedConsentIsReported()
    {
        using var server = new OAuthLoopbackServer("s"); var url = server.Start();
        await Status(new Uri(url.AbsoluteUri + "?error=access_denied&state=s"));
        Assert.Equal("access_denied", (await server.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None)).Error);
    }

    [Fact] public async Task L43ARequestDeliveredInTinyPiecesIsStillUnderstood()
    {
        using var server = new OAuthLoopbackServer("s"); var url = server.Start();
        var request = "GET /callback?code=PIECES&state=s HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
        var reply = await Task.Run(() => Raw(url.Port, request.Select(c => new[] { (byte)c }), gapMs: 1));
        Assert.StartsWith("HTTP/1.1 200", reply);
        Assert.Equal("PIECES", (await server.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None)).Code);
    }

    [Fact] public async Task L44AnEndlessHeaderIsCutOff()
    {
        using var server = new OAuthLoopbackServer("s"); var url = server.Start();
        var junk = Encoding.ASCII.GetBytes("GET /callback?state=s HTTP/1.1\r\nX: " + new string('a', 200_000));
        var started = DateTime.UtcNow;
        var reply = await Task.Run(() => Raw(url.Port, [junk]));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2.5));
        Assert.StartsWith("HTTP/1.1 431", reply);
    }

    [Fact] public async Task L45GarbageNeitherCrashesNorSettles()
    {
        using var server = new OAuthLoopbackServer("s"); var url = server.Start();
        await Task.Run(() => Raw(url.Port, [[0xFF, 0x00, 0x13, 0x37, 13, 10, 13, 10]], readTimeoutMs: 1000));
        await Status(new Uri(url.AbsoluteUri + "?code=AFTER&state=s"));
        Assert.Equal("AFTER", (await server.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None)).Code);
    }

    [Fact] public async Task L46ASecondCallbackDoesNotReplaceTheFirst()
    {
        using var server = new OAuthLoopbackServer("s"); var url = server.Start();
        await Status(new Uri(url.AbsoluteUri + "?code=FIRST&state=s"));
        try { await Status(new Uri(url.AbsoluteUri + "?code=SECOND&state=s")); } catch (HttpRequestException) { }
        Assert.Equal("FIRST", (await server.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None)).Code);
    }

    [Fact] public async Task L47WaitingTimesOutInsteadOfHanging()
    {
        using var server = new OAuthLoopbackServer("s"); server.Start();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }

    [Fact] public void L48TheRedirectListensOnLoopbackOnly()
    {
        using var server = new OAuthLoopbackServer("s"); var url = server.Start();
        Assert.Equal("127.0.0.1", url.Host);
    }

    [Fact] public async Task L49TheWholeGoogleSignInStoresRefreshableCredentials()
    {
        var client = new GoogleOAuthClient("123-abc.apps.googleusercontent.com", "GOCSPX-" + new string('s', 28));
        var tokens = new Script((HttpStatusCode.OK, """{"access_token":"ya29.x","refresh_token":"1//r","expires_in":3599,"id_token":"e.y.z"}"""));
        string? saved = null;
        var outcome = await AntigravityGoogleSignIn.RunAsync(client, new HttpClient(tokens), _ => { }, auth =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(auth.Query);
            Assert.Equal("offline", query["access_type"]);
            _ = Task.Run(async () => await Status(new Uri(query["redirect_uri"] + "?code=AUTH&state=" + query["state"])));
            return true;
        }, json => saved = json, CancellationToken.None, TimeSpan.FromSeconds(5));
        Assert.True(outcome.SignedIn, outcome.Reason);
        using var document = JsonDocument.Parse(saved!);
        Assert.Equal("1//r", document.RootElement.GetProperty("refresh_token").GetString());
        Assert.Equal(client.ClientSecret, document.RootElement.GetProperty("client_secret").GetString());
        Assert.Contains("grant_type=authorization_code", tokens.Requests.Single());
    }

    [Fact] public async Task L50AGoogleRedirectWithAForgedStateNeverReachesTheTokenEndpoint()
    {
        var client = new GoogleOAuthClient("1-a.apps.googleusercontent.com", "GOCSPX-" + new string('s', 28));
        var tokens = new Script((HttpStatusCode.OK, """{"access_token":"x"}"""));
        var outcome = await AntigravityGoogleSignIn.RunAsync(client, new HttpClient(tokens), _ => { }, auth =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(auth.Query);
            _ = Task.Run(async () => { try { await Status(new Uri(query["redirect_uri"] + "?code=AUTH&state=forged")); } catch (HttpRequestException) { } });
            return true;
        }, _ => { }, CancellationToken.None, TimeSpan.FromSeconds(5));
        Assert.False(outcome.SignedIn); Assert.Empty(tokens.Requests);
    }

    [Fact] public async Task L51GoogleRejectingTheCodeIsAReason()
    {
        var client = new GoogleOAuthClient("1-a.apps.googleusercontent.com", "GOCSPX-" + new string('s', 28));
        var tokens = new Script((HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        var outcome = await AntigravityGoogleSignIn.RunAsync(client, new HttpClient(tokens), _ => { }, auth =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(auth.Query);
            _ = Task.Run(async () => await Status(new Uri(query["redirect_uri"] + "?code=AUTH&state=" + query["state"])));
            return true;
        }, _ => { }, CancellationToken.None, TimeSpan.FromSeconds(5));
        Assert.False(outcome.SignedIn); Assert.Contains("refused", outcome.Reason);
    }

    // Level 3 — hostile text

    [Theory]
    [InlineData("gh auth login; del C:\\")]
    [InlineData("gh $(whoami)")]
    [InlineData("tool `calc`")]
    [InlineData("tool | more")]
    [InlineData("tool \"quoted\"")]
    public void L52AShellCannotBeHandedInjectedCommands(string command) => Assert.False(ProviderSignIn.IsPlainCommand(command));

    [Fact] public void L53ToolLookupHandlesBlanksAndAbsolutePaths()
    {
        Assert.False(ProviderSignIn.IsInstalled(""));
        Assert.False(ProviderSignIn.IsInstalled(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid())));
        var file = Path.GetTempFileName();
        try { Assert.True(ProviderSignIn.IsInstalled(file)); } finally { File.Delete(file); }
    }
}
