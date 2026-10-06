using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

/// <summary>
/// Adding each catalogue provider, through every way the first sign-in can go. Adding must never
/// leave a dead end: an account already present connects without opening anything, a missing
/// one starts exactly one sign-in, and cancelling, failing to open, or a missing tool each end
/// with a reason and a way to try again.
/// </summary>
public sealed class ProviderAddFlowMatrixTests
{
    public enum Flow { AlreadySignedIn, SignsInLater, Cancelled, CannotOpen, ToolMissing, NeverSignsIn, Superseded }

    public static IEnumerable<object[]> Cases() =>
        from provider in ProviderCatalog.All from flow in Enum.GetValues<Flow>() select new object[] { provider.Id, flow };
    public static IEnumerable<object[]> Providers() => ProviderCatalog.All.Select(provider => new object[] { provider.Id });

    private static SignInPlan PlanFor(string id)
    {
        // The in-app flows need a network; here they stand for "a sign-in CodeRim starts".
        var plan = ProviderSignIn.For(id);
        return plan.Kind == SignInKind.InApp ? plan with { Kind = SignInKind.Browser, Url = new Uri("https://example.invalid/") } : plan;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AddingEveryProviderNeverEndsInADeadEnd(string id, Flow flow)
    {
        var plan = PlanFor(id);
        var launches = 0; var installPages = 0; var checks = 0;
        var signedInAfter = flow switch { Flow.AlreadySignedIn => 0, Flow.SignsInLater or Flow.Superseded => 3, _ => int.MaxValue };
        ProviderConnector? connector = null;
        connector = new ProviderConnector(
            _ => Task.FromResult(++checks > signedInAfter),
            _ => { launches++; return flow != Flow.CannotOpen; },
            TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(flow == Flow.NeverSignsIn ? 120 : 3000),
            plan: _ => plan,
            preflight: p => flow == Flow.ToolMissing && p.Kind == SignInKind.Terminal ? ProviderSignIn.MissingTool(p, _ => false) : null,
            openInstallPage: _ => installPages++);
        var run = connector.RunAsync(id);
        if (flow == Flow.Cancelled) { await Task.Delay(30, TestContext.Current.CancellationToken); connector.Cancel(id); }
        if (flow == Flow.Superseded) { await Task.Delay(10, TestContext.Current.CancellationToken); await connector.RunAsync(id); }
        await run;
        var state = connector.StateOf(id);

        switch (flow)
        {
            case Flow.AlreadySignedIn:
                Assert.Equal(ProviderConnector.Phase.Connected, state?.Phase);
                Assert.Equal(0, launches); // nothing opens when the account is already there
                break;
            case Flow.Cancelled:
                Assert.Null(state); // cancelling clears the provider's sign-in instead of leaving it waiting
                break;
            case Flow.SignsInLater or Flow.Superseded:
                if (plan.OpensSettings) Assert.Equal(ProviderConnector.Phase.NeedsKey, state?.Phase);
                else Assert.Equal(ProviderConnector.Phase.Connected, state?.Phase);
                Assert.True(launches <= (flow == Flow.Superseded ? 2 : 1), $"{id} opened its sign-in {launches} times");
                break;
            case Flow.CannotOpen:
                if (plan.Kind is SignInKind.Browser or SignInKind.Terminal)
                {
                    Assert.Equal(ProviderConnector.Phase.Failed, state?.Phase);
                    Assert.Contains("could not be opened", state!.Message, StringComparison.Ordinal);
                }
                break;
            case Flow.ToolMissing:
                if (plan.Kind == SignInKind.Terminal)
                {
                    Assert.Equal(ProviderConnector.Phase.Failed, state?.Phase);
                    Assert.Contains("not installed", state!.Message, StringComparison.Ordinal);
                    Assert.Equal(0, launches); // no empty terminal for a command that does not exist
                    Assert.Equal(plan.InstallUrl is null ? 0 : 1, installPages);
                }
                break;
            case Flow.NeverSignsIn:
                if (plan.OpensSettings) Assert.Equal(ProviderConnector.Phase.NeedsKey, state?.Phase);
                else
                {
                    Assert.Equal(ProviderConnector.Phase.Failed, state?.Phase);
                    Assert.Contains("try again", state!.Message, StringComparison.OrdinalIgnoreCase);
                }
                break;
        }
        if (state is not null && state.Phase is not ProviderConnector.Phase.Connected and not ProviderConnector.Phase.Checking)
            Assert.False(string.IsNullOrWhiteSpace(state.Message), $"{id}/{flow} left {state.Phase} without saying what to do");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void EverySignInRouteIsUsable(string id)
    {
        var plan = ProviderSignIn.For(id);
        Assert.False(string.IsNullOrWhiteSpace(plan.Note), id);
        Assert.DoesNotContain("TODO", plan.Note, StringComparison.OrdinalIgnoreCase);
        Assert.True(plan.Note.Length < 600, $"{id} note is too long to read in the banner");
        switch (plan.Kind)
        {
            case SignInKind.Terminal:
                Assert.True(ProviderSignIn.IsPlainCommand(plan.Command!), id);
                Assert.NotNull(plan.InstallUrl); // a missing tool always has somewhere to get it
                Assert.Equal(Uri.UriSchemeHttps, plan.InstallUrl!.Scheme);
                break;
            case SignInKind.Browser:
                Assert.Equal(Uri.UriSchemeHttps, plan.Url!.Scheme);
                break;
            case SignInKind.Settings:
                // A key route needs a field to paste the key into.
                Assert.True(id == "claude" || NativeProviders.Supported.Contains(id) || HttpProviders.Supported.Contains(id) || ScriptProviders.Catalog.ContainsKey(id), id);
                break;
            case SignInKind.InApp:
                Assert.NotNull(plan.InApp);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void EveryProviderHasAWindowsConnection(string id) =>
        Assert.True(id is "codex" or "claude" or "jetbrains" || NativeProviders.Supported.Contains(id)
            || HttpProviders.Supported.Contains(id) || ScriptProviders.Catalog.ContainsKey(id), $"{id} has no Windows reader");

    [Theory]
    [MemberData(nameof(Providers))]
    public void EveryProviderHasANameSummaryAndUniqueId(string id)
    {
        var provider = ProviderCatalog.Find(id)!;
        Assert.False(string.IsNullOrWhiteSpace(provider.Name), id);
        Assert.False(string.IsNullOrWhiteSpace(provider.Summary), id);
        Assert.Single(ProviderCatalog.All, x => x.Id == id);
    }

    [Fact]
    public void KiroSignsInThroughItsCli()
    {
        var plan = ProviderSignIn.For("kiro");
        Assert.Equal(SignInKind.Terminal, plan.Kind); Assert.Equal("kiro-cli login", plan.Command);
        Assert.Equal("https://kiro.dev/cli/", plan.InstallUrl!.AbsoluteUri);
    }

    [Fact]
    public async Task JetBrainsExplainsTheIdeStepAndWatchesForIt()
    {
        var plan = ProviderSignIn.For("jetbrains");
        Assert.Equal(SignInKind.Guidance, plan.Kind);
        Assert.Contains("JetBrains IDE", plan.Note, StringComparison.Ordinal);
        var launches = 0; var checks = 0;
        var connector = new ProviderConnector(_ => Task.FromResult(++checks > 2), _ => { launches++; return true; },
            TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(3));
        await connector.RunAsync("jetbrains");
        Assert.Equal(ProviderConnector.Phase.Connected, connector.StateOf("jetbrains")?.Phase);
        Assert.Equal(0, launches);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GeminiCliExplainsApiKeyMode(bool usesKey)
    {
        var previous = ProviderSignIn.GeminiUsesKey;
        try
        {
            ProviderSignIn.GeminiUsesKey = () => usesKey;
            var plan = ProviderSignIn.For("gemini-cli");
            Assert.Equal(SignInKind.Terminal, plan.Kind);
            Assert.Equal(usesKey, plan.Note.Contains("set to an API key", StringComparison.Ordinal));
            Assert.Equal(usesKey, plan.Hint.Contains("/auth", StringComparison.Ordinal));
            Assert.Contains("/quit", plan.Hint, StringComparison.Ordinal);
        }
        finally { ProviderSignIn.GeminiUsesKey = previous; }
    }

    [Theory]
    [InlineData("""{"security":{"auth":{"selectedType":"gemini-api-key"}}}""", true)]
    [InlineData("""{"security":{"auth":{"selectedType":"vertex-ai"}}}""", true)]
    [InlineData("""{"security":{"auth":{"selectedType":"oauth-personal"}}}""", false)]
    [InlineData("""{}""", false)]
    [InlineData("{ // comment\n \"security\": {\"auth\": {\"selectedType\": \"gemini-api-key\",}}}", true)]
    [InlineData("not json", false)]
    public void GeminiKeyModeIsReadFromTheCliSettings(string settings, bool usesKey)
    {
        var home = Home();
        try
        {
            Directory.CreateDirectory(Path.Combine(home.FullName, ".gemini"));
            File.WriteAllText(Path.Combine(home.FullName, ".gemini", "settings.json"), settings);
            Assert.Equal(usesKey, GeminiAuthentication.UsesKey(home.FullName));
        }
        finally { home.Delete(true); }
    }

    // macOS temp folders sit behind the /var link, which the guarded reader refuses like any reparse point.
    private static DirectoryInfo Home()
    {
        var created = Directory.CreateTempSubdirectory("coderim-gemini-");
        return created.FullName.StartsWith("/var/", StringComparison.Ordinal) ? new DirectoryInfo("/private" + created.FullName) : created;
    }

    [Fact]
    public void GeminiWithoutSettingsIsNotKeyMode()
    {
        var home = Home();
        try { Assert.False(GeminiAuthentication.UsesKey(home.FullName)); } finally { home.Delete(true); }
    }
}
