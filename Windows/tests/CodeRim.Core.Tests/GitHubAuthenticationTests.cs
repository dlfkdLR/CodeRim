using CodeRim.Core.Services;
namespace CodeRim.Core.Tests;

public sealed class GitHubAuthenticationTests
{
    [Fact]
    public void ChoosesOnlyActiveGithubUser()
    {
        const string hosts = """
            enterprise.invalid:
                oauth_token: enterprise
            github.com:
                users:
                    active:
                        oauth_token: active-token
                    inactive:
                        oauth_token: other-token
                user: active
                git_protocol: https
            """;
        Assert.Equal("active-token", GitHubAuthentication.ParseHosts(hosts));
        Assert.Null(GitHubAuthentication.ParseHosts(hosts.Replace("    user: active", "    user: missing", StringComparison.Ordinal)));
        Assert.Null(GitHubAuthentication.ParseHosts(hosts.Replace("    user: active", "", StringComparison.Ordinal)));
    }
    [Theory]
    [InlineData("github.com:\n    oauth_token: 'opaque'\n", "opaque")]
    [InlineData("github.com:\n    oauth_token: \"opaque\"\n", "opaque")]
    [InlineData("github.com:\n    oauth_token: opaque # comment\n", "opaque")]
    [InlineData("github.com:\n    ignored: scalar\n        oauth_token: wrong-depth\n", null)]
    [InlineData("github.com:\n    user: active\n    git_protocol: https\n        oauth_token: wrong-depth\n", null)]
    [InlineData("github.com:\n    user: active\n  oauth_token: wrong-depth\n", null)]
    [InlineData("github.com:\n    users:\n        active:\n            oauth_token: first\n      inactive:\n          oauth_token: wrong-depth\n    user: active\n", null)]
    [InlineData("enterprise.invalid:\n    oauth_token: other\n", null)]
    [InlineData("github.com:\n    oauth_token: first\n    oauth_token: second\n", null)]
    [InlineData("github.com:\n    oauth_token: first\ngithub.com:\n    oauth_token: second\n", null)]
    [InlineData("github.com:\n    oauth_token: *anchor\n", null)]
    [InlineData("github.com:\n    oauth_token: \"line\\nbreak\"\n", null)]
    [InlineData("github.com:\n    oauth_token: root\n    user: active\n    users:\n        active:\n            oauth_token: other\n", null)]
    public void RejectsAmbiguousHostTokens(string text, string? expected) => Assert.Equal(expected, GitHubAuthentication.ParseHosts(text));
    [Fact]
    public void EnvironmentPrecedenceIgnoresBlankFirstAlias()
    {
        Assert.Equal("saved", GitHubAuthentication.Configured(" saved ", "gh", "github"));
        Assert.Equal("github", GitHubAuthentication.Configured(null, " ", "github"));
        Assert.Null(GitHubAuthentication.Configured(" ", null, ""));
        Assert.Null(GitHubAuthentication.Token("bad\r\ntoken"));
        Assert.Null(GitHubAuthentication.ParseHosts(new string('x', 262145)));
    }
    [Fact]
    public void UsesDocumentedWindowsDirectoryOrder()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixture"));
        var env = new Dictionary<string, string?> { ["APPDATA"] = root };
        string Config() => GitHubAuthentication.ConfigurationDirectory(root, env.GetValueOrDefault, windows: true);
        Assert.Equal(Path.Combine(root, "GitHub CLI"), Config());
        env["XDG_CONFIG_HOME"] = Path.Combine(root, "xdg"); Assert.Equal(Path.Combine(root, "xdg", "gh"), Config());
        env["GH_CONFIG_DIR"] = Path.Combine(root, "gh"); Assert.Equal(Path.Combine(root, "gh"), Config());
        env["GH_CONFIG_DIR"] = "relative"; Assert.Equal(Path.Combine(root, "xdg", "gh"), Config());
    }

    [Fact]
    public void UsernameOnlyAccountDoesNotAuthorizeRequestsOrBorrowTokenIdentity()
    {
        const string hosts = "github.com:\n  user: fixture-user\n";
        var account = GitHubAuthentication.AccountSummary(hosts)!;
        Assert.Equal("fixture-user", account.Account?.Label);
        Assert.Equal("GitHub", account.Account?.Source);
        Assert.Null(account.Plan);
        Assert.Null(GitHubAuthentication.ParseHosts(hosts));
        Assert.Null(GitHubAuthentication.AccountLabel(hosts, "external-token"));
        Assert.NotEqual(account.Version, GitHubAuthentication.AccountSummary(hosts.Replace("fixture-user", "other-user", StringComparison.Ordinal))!.Version);
        Assert.NotEqual(account.Version, GitHubAuthentication.AccountSummary(hosts + "  oauth_token: fixture-token\n")!.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("enterprise.invalid:\n  user: fixture-user\n")]
    [InlineData("github.com:\n  user: fixture-user\n  user: other\n")]
    [InlineData("github.com:\n  user: fixture-user\ngithub.com:\n  user: other\n")]
    [InlineData("github.com:\n  user: fixture-user\n    oauth_token: wrong-indent\n")]
    [InlineData("github.com:\n\tuser: fixture-user\n")]
    [InlineData("github.com:\n  user: *alias\n")]
    [InlineData("github.com:\n  user: \"bad\\nname\"\n")]
    [InlineData("github.com:\n  user: fixture-user\n  oauth_token: root-token\n  users:\n    fixture-user:\n      oauth_token: conflicting-token\n")]
    [InlineData("github.com:\n  user: fixture-user\n  oauth_token: \"bad\\ntoken\"\n")]
    public void DisplaySummaryRejectsMalformedOrAmbiguousHosts(string? hosts)
        => Assert.Null(GitHubAuthentication.AccountSummary(hosts));

    [Fact]
    public void DisplaySummaryIsBoundedAndDoesNotSerializeCredentialOrVersion()
    {
        Assert.Null(GitHubAuthentication.AccountSummary("github.com:\n  user: " + new string('x', 257)));
        Assert.Null(GitHubAuthentication.AccountSummary("github.com:\n  user: fixture\n#" + new string('x', 262144)));
        var summary = GitHubAuthentication.AccountSummary("github.com:\n  user: fixture\n  oauth_token: fixture-secret")!;
        var serialized = System.Text.Json.JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("fixture-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(summary.Version, serialized, StringComparison.Ordinal);
        Assert.Equal("Detected provider connection", summary.ToString());
        var configured = NativeAccountSummary.FromCredential("copilot", "fixture-secret")!;
        Assert.Null(configured.Account?.Label);
        Assert.Equal("GitHub", configured.Account?.Source);
        Assert.NotEqual(summary.Version, configured.Version);
    }
}
