using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

[Collection("Native process execution")]
public sealed class CodexHomeDirectoryTests
{
    [Theory]
    [InlineData(null)] [InlineData("")]
    public void MissingOrEmptyHomeUsesTheKnownUserProfile(string? configured)
    {
        var profile = Path.Combine(Path.GetTempPath(), "known-profile");
        var expected = Path.Combine(profile, ".codex");
        Assert.Equal(expected, CodexHomeDirectory.Resolve(configured, profile));
        Assert.Equal(Path.Combine(expected, "auth.json"), CodexHomeDirectory.CredentialPath(configured, profile));
    }

    [Fact]
    public void CustomUnicodeHomePreservesSpacesAndDoesNotTrimTheCliValue()
    {
        var custom = Path.Combine(Path.GetTempPath(), " 계정 😀 ");
        Assert.Equal(custom, CodexHomeDirectory.Resolve(custom, Path.GetTempPath()));
        Assert.Equal(Path.Combine(custom, "auth.json"), CodexHomeDirectory.CredentialPath(custom, Path.GetTempPath()));
    }

    [Theory]
    [InlineData("relative-home")] [InlineData(" ")] [InlineData(".")] [InlineData("../other")]
    public void RelativeHomeCannotSelectLoginFileFromTheWorkingDirectory(string configured)
    {
        // Statistics retain the CLI's configured path; account mutation requires
        // an absolute destination and must never silently fall back to the profile.
        Assert.Equal(configured, CodexHomeDirectory.Resolve(configured, Path.GetTempPath()));
        Assert.Throws<InvalidOperationException>(() => CodexHomeDirectory.CredentialPath(configured, Path.GetTempPath()));
    }

    [Theory]
    [InlineData(null)] [InlineData("")]
    public void ScannerUsesTheActualEmptyEnvironmentFallback(string? configured)
    {
        var before = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", configured);
            Assert.Equal(configured, Environment.GetEnvironmentVariable("CODEX_HOME"));
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            Assert.Equal(new[] { Path.Combine(expected, "sessions"), Path.Combine(expected, "archived_sessions") }, UsageScanner.DefaultRoots());
        }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME", before); }
    }

    [Theory]
    [InlineData("")] [InlineData("relative-profile")]
    public void UnavailableKnownProfileDoesNotFallBackToWorkingDirectory(string profile)
        => Assert.Throws<InvalidOperationException>(() => CodexHomeDirectory.CredentialPath(null, profile));

    public static bool IsWindows => OperatingSystem.IsWindows();
    [Theory(Skip = "Windows drive-relative path semantics", SkipUnless = nameof(IsWindows))]
    [InlineData("C:relative")] [InlineData("\\relative")] [InlineData("/relative")]
    public void RootedButNotFullyQualifiedHomeIsRejectedOnWindows(string configured)
        => Assert.Throws<InvalidOperationException>(() => CodexHomeDirectory.CredentialPath(configured, Path.GetTempPath()));
}
