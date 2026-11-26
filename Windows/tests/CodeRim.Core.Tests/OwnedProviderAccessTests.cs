using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using Xunit;

namespace CodeRim.Core.Tests;

public sealed class OwnedProviderAccessTests
{
    private static readonly string[] GoogleFields = ["ANTIGRAVITY_OAUTH_CLIENT_ID", "ANTIGRAVITY_OAUTH_CLIENT_SECRET"];
    private static readonly string[] DeepSeekKeys = ["DEEPSEEK_API_KEY", "DEEPSEEK_KEY"];

    [Theory]
    [InlineData("cursor")]
    [InlineData("jetbrains")]
    [InlineData("kiro")]
    [InlineData("perplexity")]
    [InlineData("stepfun")]
    [InlineData("gemini-cli")]
    public void ExternalOnlyProvidersDoNotSearchForCredentials(string id)
    {
        var plan = OwnedProviderAccess.Plan(id, _ => throw new InvalidOperationException());
        Assert.Empty(OwnedProviderAccess.CredentialKeys(id));
        Assert.True(plan.OpensSettings);
        Assert.Equal(OwnedProviderAccess.Unavailable, plan.Note);
    }

    [Fact]
    public void DeviceAndExplicitGoogleConsentKeepTheirIssuedRoutes()
    {
        Assert.Equal(InAppKind.GitHubDevice, OwnedProviderAccess.Plan("copilot", _ => throw new InvalidOperationException()).InApp);
        var reads = new List<string>();
        var absent = OwnedProviderAccess.Plan("gemini", k => { reads.Add(k); return null; });
        Assert.True(absent.OpensSettings);
        Assert.Equal(GoogleFields, reads);
        var configured = OwnedProviderAccess.Plan("gemini", _ => "synthetic-configured");
        Assert.Equal(InAppKind.AntigravityGoogle, configured.InApp);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public void SubscriptionLoginRequiresSelectingOrAddingAnAccountInCodeRim(string id)
    {
        var plan = OwnedProviderAccess.Plan(id, _ => throw new InvalidOperationException());
        Assert.True(plan.OpensSettings);
        Assert.Null(plan.Command);
    }

    [Theory]
    [InlineData("GH_TOKEN", false)]
    [InlineData("ANTIGRAVITY_OAUTH_CREDENTIALS_JSON", false)]
    [InlineData("GROK_OAUTH_TOKEN", false)]
    [InlineData("DEEPSEEK_KEY", true)]
    [InlineData("OPENROUTER_API_KEY", true)]
    public void OnlyExplicitApiKeyVariablesCanSupplyCredentials(string name, bool allowed) =>
        Assert.Equal(allowed, OwnedProviderAccess.IsExplicitApiKey(name));

    [Fact]
    public void EveryCatalogProviderHasAnExplicitPlanAndKeysExcludeCopiedSessions()
    {
        foreach (var provider in ProviderCatalog.All)
        {
            var plan = OwnedProviderAccess.Plan(provider.Id, _ => null);
            Assert.NotEmpty(plan.Note);
            Assert.DoesNotContain("Firefox", plan.Note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Chromium", plan.Note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(OwnedProviderAccess.CredentialKeys(provider.Id), k =>
                k.Contains("COOKIE", StringComparison.Ordinal) || k.Contains("SESSION", StringComparison.Ordinal));
        }
        Assert.Equal(DeepSeekKeys, OwnedProviderAccess.CredentialKeys("deepseek"));
    }
}
