using System.Net;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;

public sealed class ProviderRetryScopeTests
{
    [Theory]
    [InlineData("api", "account-b")]
    [InlineData("web", "account-a")]
    public async Task HttpCooldownDoesNotTransferToAnotherAccountOrSource(string source, string credential)
    {
        var calls = 0;
        using var provider = new HttpProviders(new Handler(_ => {
            calls++; return calls == 1 ? new(HttpStatusCode.TooManyRequests)
                : new(HttpStatusCode.OK) { Content = new StringContent(source == "web"
                    ? """{"data":{"biz_data":{"normal_wallets":[],"bonus_wallets":[]}}}"""
                    : """{"balance_infos":[]}""") };
        }));
        Assert.Equal(ReadingState.Unavailable, (await provider.FetchAsync("deepseek", "account-a", _ => "api", TestContext.Current.CancellationToken)).State);
        Assert.Equal(ReadingState.Ready, (await provider.FetchAsync("deepseek", credential, _ => source, TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, calls);
        Assert.Equal(ReadingState.Unavailable, (await provider.FetchAsync("deepseek", "account-a", _ => "api", TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCooldownFollowsTheActualCredential(bool browser)
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(_ => {
            calls++; return calls == 1 ? new(HttpStatusCode.TooManyRequests)
                : new(HttpStatusCode.OK) { Content = new StringContent("""{"remainingBalance":25}""") };
        }));
        Task<ProviderReading> Fetch(string token) => provider.FetchAsync("codebuff", browser ? null : token, key => key == "CODEBUFF_INCLUDE_SUBSCRIPTION" ? "false" : null,
            browser ? _ => "session=" + token : null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Unavailable, (await Fetch("account-a")).State);
        Assert.Equal(ReadingState.Ready, (await Fetch("account-b")).State);
        Assert.Equal(2, calls);
        Assert.Equal(ReadingState.Unavailable, (await Fetch("account-a")).State);
        Assert.Equal(2, calls);
    }
    [Theory]
    [InlineData("fireworks")]
    [InlineData("vertexai")]
    public async Task RollingTimeQueriesCannotEvadeTheSameAccountCooldown(string id)
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(_ => { calls++; return new(HttpStatusCode.TooManyRequests); }));
        string? Setting(string key) => key is "FIREWORKS_ACCOUNT_SLUG" or "GOOGLE_CLOUD_PROJECT" ? "fixture-project" : null;
        Assert.Equal(ReadingState.Unavailable, (await provider.FetchAsync(id, "account", Setting, TestContext.Current.CancellationToken)).State);
        await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Unavailable, (await provider.FetchAsync(id, "account", Setting, TestContext.Current.CancellationToken)).State);
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task AzureLegacyDeploymentAliasIsPartOfTheEffectiveScope()
    {
        var calls = 0; var deployment = "a";
        using var provider = new NativeProviders(new Handler(_ => { calls++; return calls == 1 ? new(HttpStatusCode.TooManyRequests)
            : new(HttpStatusCode.OK) { Content = new StringContent("{}") }; }));
        string? Setting(string key) => key switch { "AZURE_OPENAI_ENDPOINT" => "https://fixture.openai.azure.com",
            "AZURE_OPENAI_API_VERSION" => "v1", "AZURE_OPENAI_DEPLOYMENT" => deployment, "AZURE_OPENAI_ALLOW_BILLABLE_REQUESTS" => "true", _ => null };
        Assert.Equal(ReadingState.Unavailable, (await provider.FetchAsync("azureopenai", "account", Setting, TestContext.Current.CancellationToken)).State);
        deployment = "b";
        Assert.Equal(ReadingState.Ready, (await provider.FetchAsync("azureopenai", "account", Setting, TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, calls);
        deployment = "a";
        Assert.Equal(ReadingState.Unavailable, (await provider.FetchAsync("azureopenai", "account", Setting, TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, calls);
    }
    [Fact]
    public async Task KiroBundledProfileIsPartOfTheEffectiveScope()
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(_ => { calls++; return calls == 1 ? new(HttpStatusCode.TooManyRequests)
            : new(HttpStatusCode.OK) { Content = new StringContent("""{"usageBreakdownList":[]}""") }; }));
        Task<ProviderReading> Fetch(string profile) => provider.FetchAsync("kiro",
            System.Text.Json.JsonSerializer.Serialize(new { access_token = "account", profileArn = "arn:aws:codewhisperer:us-east-1:123456789012:profile/" + profile }),
            _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Unavailable, (await Fetch("a")).State);
        await Fetch("b"); Assert.Equal(2, calls);
        Assert.Equal(ReadingState.Unavailable, (await Fetch("a")).State); Assert.Equal(2, calls);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply(request));
    }
}
