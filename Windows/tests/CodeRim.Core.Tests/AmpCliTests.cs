using System.Net;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
namespace CodeRim.Core.Tests;
public sealed class AmpCliTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsUsageFromStdoutOrStderrWithoutInventingTokens(bool stderr)
    {
        const string text = "Amp Free: 75% remaining today\nIndividual credits: $12.50 remaining\nWorkspace login project: $8.00 remaining";
        var result = AmpCliUsage.Parse(new(0, stderr ? "" : text, stderr ? text : ""));
        Assert.Equal(ReadingState.Ready, result.State);
        Assert.Equal(25, result.Windows.First(x => x.Id == "free").UsedPercent);
        Assert.Contains(result.Windows, x => x.Name == "login project" && x.Unit == "USD");
    }
    [Theory]
    [InlineData("Please run amp login", ReadingState.NeedsAuth)]
    [InlineData("not logged in", ReadingState.NeedsAuth)]
    [InlineData("network request failed fixture-secret", ReadingState.Error)]
    public void FailureOutputIsClassifiedButNeverExposed(string error, ReadingState expected)
    {
        var reading = AmpCliUsage.Parse(new(1, "", error));
        Assert.Equal(expected, reading.State); Assert.Empty(reading.Windows);
        Assert.DoesNotContain("fixture-secret", reading.Message ?? "");
    }
    [Fact]
    public void FailedCommandCannotPublishValidLookingPartialStdout()
    {
        var reading = AmpCliUsage.Parse(new(1, "Amp Free: 75% remaining today", "network failed"));
        Assert.Equal(ReadingState.Error, reading.State); Assert.Empty(reading.Windows);
        Assert.Equal(ReadingState.Unavailable, AmpCliUsage.Parse(new(0, "", "")).State);
    }
    [Theory]
    [InlineData(null, "api")]
    [InlineData(" ", "api")]
    [InlineData(" CLI ", "cli")]
    [InlineData("auto", null)]
    public void SourceIsExplicitAndPreservesTheExistingApiDefault(string? value, string? expected) =>
        Assert.Equal(expected, AmpCliUsage.Source(value));
    [Fact]
    public async Task StructuredProcessPreservesBothStreamsAndExitWithoutChangingLegacyFailure()
    {
        var executable = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";
        string[] args = OperatingSystem.IsWindows() ? ["/d", "/c", "(echo fixture-output)&(echo fixture-error 1>&2)&exit /b 3"]
            : ["-c", "printf fixture-output; printf fixture-error >&2; exit 3"];
        var result = await BoundedProcess.RunResultAsync(executable, args, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, result.ExitCode); Assert.Contains("fixture-output", result.Output); Assert.Contains("fixture-error", result.Error);
        var error = await Assert.ThrowsAsync<IOException>(() => BoundedProcess.RunAsync(executable, args, cancellationToken: TestContext.Current.CancellationToken));
        Assert.DoesNotContain("fixture-error", error.Message);
    }
    [Fact]
    public async Task DocumentedAntigravityJsonAliasUsesTheExistingOauthParser()
    {
        var keys = NativeProviders.CredentialKeys("gemini")!;
        var values = new Dictionary<string, string> { ["ANTIGRAVITY_OAUTH_CREDENTIALS_JSON"] = """{"accessToken":"alias-fixture","expiry_date":4102444800000}""" };
        var credential = keys.Select(k => values.GetValueOrDefault(k)).FirstOrDefault(x => x is { Length: > 0 });
        Assert.NotNull(credential);
        Assert.Equal("ANTIGRAVITY_OAUTH_ACCESS_TOKEN", keys[0]);
        using var provider = new NativeProviders(new AliasHandler());
        var reading = await provider.FetchAsync("gemini", credential, _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(50, reading.Headline!.UsedPercent);
    }
    private sealed class AliasHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("Bearer alias-fixture", request.Headers.Authorization!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                request.RequestUri!.AbsolutePath.EndsWith(":loadCodeAssist", StringComparison.Ordinal) ? "{}" :
                """{"models":{"fixture":{"quotaInfo":{"remainingFraction":0.5}}}}""") });
        }
    }
}
