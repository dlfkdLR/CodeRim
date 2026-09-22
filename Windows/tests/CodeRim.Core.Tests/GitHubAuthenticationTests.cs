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
}
