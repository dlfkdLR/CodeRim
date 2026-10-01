using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class ProviderSignInTests
{
    [Fact]
    public void EveryProviderHasAWayToGetSignedInWithoutADeadEnd()
    {
        foreach (var provider in ProviderCatalog.All)
        {
            var plan = ProviderSignIn.For(provider.Id);
            Assert.False(string.IsNullOrWhiteSpace(plan.Note), provider.Id);
            switch (plan.Kind)
            {
                case SignInKind.Terminal:
                    Assert.True(ProviderSignIn.IsPlainCommand(plan.Command!), provider.Id);
                    break;
                case SignInKind.Browser:
                    Assert.Equal(Uri.UriSchemeHttps, plan.Url!.Scheme);
                    break;
                case SignInKind.Settings:
                    Assert.True(plan.OpensSettings);
                    break;
            }
        }
    }

    [Theory]
    [InlineData("codex", SignInKind.Terminal, "codex login")]
    [InlineData("copilot", SignInKind.Terminal, "gh auth login --web")]
    [InlineData("grok", SignInKind.Terminal, "grok login")]
    [InlineData("gemini", SignInKind.Browser, null)]
    [InlineData("perplexity", SignInKind.Browser, null)]
    [InlineData("deepseek", SignInKind.Settings, null)]
    [InlineData("openai", SignInKind.Settings, null)]
    public void KnownProvidersUseTheirOwnSignIn(string id, SignInKind kind, string? command)
    {
        var plan = ProviderSignIn.For(id);
        Assert.Equal(kind, plan.Kind); Assert.Equal(command, plan.Command);
    }

    [Theory]
    [InlineData("gh auth login --web", true)]
    [InlineData("gcloud auth application-default login", true)]
    [InlineData("gh auth login; del C:\\", false)]
    [InlineData("echo %USERPROFILE%", false)]
    [InlineData("a && b", false)]
    [InlineData("", false)]
    public void OnlyPlainCommandsReachAShell(string command, bool allowed) => Assert.Equal(allowed, ProviderSignIn.IsPlainCommand(command));

    private static ProviderConnector Make(Func<string, Task<bool>> connects, Func<SignInPlan, bool>? launch = null, int patienceMs = 5000)
        => new(connects, launch ?? (_ => true), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(patienceMs));

    [Fact]
    public async Task AnExistingSignInConnectsWithoutOpeningAnything()
    {
        var launched = 0;
        var connector = Make(_ => Task.FromResult(true), _ => { launched++; return true; });
        await connector.RunAsync("codex");
        Assert.Equal(ProviderConnector.Phase.Connected, connector.StateOf("codex")!.Phase); Assert.Equal(0, launched);
    }

    [Fact]
    public async Task SignInOpensOnceThenIsWatchedUntilTheAccountAppears()
    {
        int checks = 0, launched = 0;
        var connector = Make(_ => Task.FromResult(++checks >= 4), _ => { launched++; return true; });
        await connector.RunAsync("perplexity");
        Assert.Equal(ProviderConnector.Phase.Connected, connector.StateOf("perplexity")!.Phase);
        Assert.Equal(1, launched); Assert.True(checks >= 4);
    }

    [Fact]
    public async Task AKeyProviderGoesStraightToItsSettings()
    {
        var launched = 0;
        var connector = Make(_ => Task.FromResult(false), _ => { launched++; return true; });
        await connector.RunAsync("deepseek");
        Assert.Equal(ProviderConnector.Phase.NeedsKey, connector.StateOf("deepseek")!.Phase); Assert.Equal(0, launched);
    }

    [Fact]
    public async Task AnUnopenableSignInIsExplainedAndAMissingOneTimesOut()
    {
        var failed = Make(_ => Task.FromResult(false), _ => false);
        await failed.RunAsync("codex");
        Assert.Equal(ProviderConnector.Phase.Failed, failed.StateOf("codex")!.Phase);

        var timeout = Make(_ => Task.FromResult(false), patienceMs: 60);
        await timeout.RunAsync("codex");
        Assert.Equal(ProviderConnector.Phase.Failed, timeout.StateOf("codex")!.Phase);
    }

    [Fact]
    public async Task CancellingStopsTheWatchAndForgetsTheState()
    {
        var checks = 0; var changes = 0;
        var connector = Make(_ => { checks++; return Task.FromResult(false); });
        connector.Changed += () => changes++;
        var run = connector.RunAsync("codex");
        await Task.Delay(60, TestContext.Current.CancellationToken);
        connector.Cancel("codex"); await run;
        var after = checks; await Task.Delay(80, TestContext.Current.CancellationToken);
        Assert.Null(connector.StateOf("codex")); Assert.True(checks <= after + 1); Assert.True(changes >= 2);
    }
}
