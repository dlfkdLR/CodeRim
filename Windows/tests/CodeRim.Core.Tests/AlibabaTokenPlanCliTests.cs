using System.Collections;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class AlibabaTokenPlanCliTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coderim-bailian-fixture-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Theory]
    [InlineData("intl", "ap-southeast-1", "international")]
    [InlineData("intl-personal", "ap-southeast-1", "international")]
    [InlineData("cn", "cn-beijing", "domestic")]
    [InlineData("cn-personal", "cn-beijing", "domestic")]
    public void RegionUsesOnlyFixedQuotaArguments(string region, string consoleRegion, string site)
        => Assert.Equal(["usage", "token-plan", "--console-region", consoleRegion, "--console-site", site, "--output", "json"],
            AlibabaTokenPlanCliUsage.Arguments(region));
    [Theory]
    [InlineData("""{"per5HourPercentage":0,"per1WeekPercentage":1}""", 0, 2)]
    [InlineData("""{"per1WeekPercentage":0.25}""", 25, 1)]
    [InlineData("""{"per5HourPercentage":"0.25","per1WeekPercentage":0.5}""", 50, 1)]
    [InlineData("""{"per5HourPercentage":1.5,"per1WeekPercentage":0.75}""", 75, 1)]
    public void ValidWindowsAreIndependentNumbers(string json, double expected, int count)
    {
        var reading = AlibabaTokenPlanCliUsage.Parse(new(0, json, ""));
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(count, reading.Windows.Count);
        Assert.Equal(expected, reading.Headline!.UsedPercent);
        Assert.Null(reading.Headline.UsedCount); Assert.Null(reading.Headline.RemainingCount);
        Assert.Equal("Token Plan", reading.Plan);
    }
    [Theory]
    [InlineData("""{"per5HourPercentage":"0.25"}""")]
    [InlineData("""{"per5HourPercentage":true}""")]
    [InlineData("""{"per5HourPercentage":null}""")]
    [InlineData("""{"per5HourPercentage":-0.1}""")]
    [InlineData("""{"per5HourPercentage":1.1}""")]
    [InlineData("""{"per5HourPercentage":1e400}""")]
    [InlineData("""{"data":{"per5HourPercentage":0.25}}""")]
    [InlineData("""{"per5HourPercentage":0.25,"per5HourPercentage":0.5}""")]
    [InlineData("""[{"per5HourPercentage":0.25}]""")]
    [InlineData("not-json")]
    public void UnsupportedAndAmbiguousOutputCannotBecomeZeroUsage(string json)
    {
        var reading = AlibabaTokenPlanCliUsage.Parse(new(0, json, ""));
        Assert.Equal(ReadingState.Error, reading.State); Assert.Empty(reading.Windows);
    }
    [Fact]
    public void ResetIsMillisecondsWithoutMagnitudeGuessing()
    {
        var result = AlibabaTokenPlanCliUsage.Parse(new(0, """{"per5HourPercentage":0.5,"per5HourResetTime":1787000400,"per1WeekPercentage":0.25,"per1WeekResetTime":1e400}""", ""));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1787000400), result.Windows[0].ResetsAt);
        Assert.Null(result.Windows[1].ResetsAt);
    }
    [Fact]
    public void CommandFailureAndOversizeDoNotExposeCommandOutput()
    {
        foreach (var result in new[] { new ProcessResult(1, "fixture-secret", "fixture-secret"),
            new ProcessResult(0, new string('x', 65537), "fixture-secret") })
        {
            var reading = AlibabaTokenPlanCliUsage.Parse(result);
            Assert.Equal(ReadingState.Error, reading.State);
            Assert.DoesNotContain("fixture-secret", reading.Message);
        }
    }
    [Fact]
    public void ChildEnvironmentKeepsOnlyReviewedRuntimeAndProxyKeys()
    {
        var environment = AlibabaTokenPlanCliUsage.ChildEnvironment(new Hashtable
        {
            ["HOME"] = "/synthetic/home", ["SystemRoot"] = @"C:\Windows", ["HTTPS_PROXY"] = "http://127.0.0.1:8080",
            ["NODE_OPTIONS"] = "--require=fixture", ["BAILIAN_CONFIG_DIR"] = "/other-account",
            ["GITHUB_TOKEN"] = "fixture", ["ALIBABA_TOKEN_PLAN_COOKIE"] = "fixture", ["CLOUD_SECRET"] = "fixture"
        });
        Assert.Equal(4, environment.Count); Assert.Equal("1", environment["NO_COLOR"]);
        Assert.False(environment.ContainsKey("NODE_OPTIONS")); Assert.False(environment.ContainsKey("BAILIAN_CONFIG_DIR"));
        Assert.Equal(@"C:\Windows", environment["SystemRoot"]);
    }
    [Fact]
    public void ResolverRejectsRelativePathsAndResolvesOfficialStyleParentLinks()
    {
        Directory.CreateDirectory(root);
        var version = Path.Combine(root, "versions", "fixture"); Directory.CreateDirectory(version);
        File.WriteAllText(Path.Combine(version, "bl.exe"), "");
        var bin = Path.Combine(root, "bin");
        // Unix permits unprivileged symlinks; the native Windows review also checks junctions.
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(bin, version);
            Assert.Equal(AlibabaTokenPlanCliUsage.ResolveExecutable(Path.Combine(version, "bl.exe"), null, null), AlibabaTokenPlanCliUsage.ResolveExecutable(Path.Combine(bin, "bl.exe"), null, null));
        }
        Assert.Null(AlibabaTokenPlanCliUsage.ResolveExecutable("bl.exe", root, null));
        Assert.Null(AlibabaTokenPlanCliUsage.ResolveExecutable(null, "." + Path.PathSeparator + "relative", null));
        Assert.Null(AlibabaTokenPlanCliUsage.ResolveExecutable(Path.Combine(root, "other.exe"), null, null));
        Assert.EndsWith(Path.Combine("versions", "fixture", "bl.exe"), AlibabaTokenPlanCliUsage.ResolveExecutable(null, version, null));
    }
    [Fact]
    public async Task RealIsolatedChildDropsAmbientSecretsAndBoundsOutput()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "bl.exe");
        await File.WriteAllTextAsync(script, "#!/bin/sh\n[ -z \"$NODE_OPTIONS$GITHUB_TOKEN$BAILIAN_CONFIG_DIR\" ] || exit 9\nprintf '{\"per5HourPercentage\":0.25}'\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var environment = new Dictionary<string, string?> { ["HOME"] = root };
        var result = await BoundedProcess.RunIsolatedResultAsync(script, AlibabaTokenPlanCliUsage.Arguments("intl"),
            environment, TimeSpan.FromSeconds(2), 65536, TestContext.Current.CancellationToken);
        Assert.Equal(25, AlibabaTokenPlanCliUsage.Parse(result).Headline!.UsedPercent);
        await File.WriteAllTextAsync(script, "#!/bin/sh\nwhile :; do printf 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'; done\n", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => BoundedProcess.RunIsolatedResultAsync(script, [], environment,
            TimeSpan.FromSeconds(2), 1024, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task IsolatedCommandCancellationCannotBecomeAReading()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root); var script = Path.Combine(root, "bl.exe");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nsleep 5\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AlibabaTokenPlanCliUsage.ReadAsync(script, "cn", cancellation.Token));
    }
}
