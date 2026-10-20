using System.Text.Json;
using CodeRim.Core.Services;
namespace CodeRim.Core.Tests;
public sealed class KimiAuthenticationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    [Theory]
    [InlineData("1800000061", true)]
    [InlineData("\"1800000061\"", true)]
    [InlineData("1800000060", false)]
    [InlineData("null", false)]
    [InlineData("\"NaN\"", false)]
    [InlineData("1e100", false)]
    [InlineData("true", false)]
    public void CliExpiryRequiresSixtySecondsOfRemainingValidity(string expiry, bool valid)
    {
        Assert.Equal(valid, KimiAuthentication.ParseCli("""{"access_token":" fixture-token ","expires_at":""" + expiry + "}", Now) is not null);
    }
    [Theory]
    [InlineData("""{"access_token":"one","access_token":"two","expires_at":1800001000}""")]
    [InlineData("""{"access_token":123,"expires_at":1800001000}""")]
    [InlineData("""{"access_token":"one","refresh_token":{},"expires_at":1800001000}""")]
    [InlineData("""{"access_token":"bad\nheader","expires_at":1800001000}""")]
    public void AmbiguousOrUnsafeCliFilesAreRejected(string json) => Assert.Null(KimiAuthentication.ParseCli(json, Now));
    [Theory]
    [InlineData("KIMI_CODE_BASE_URL")]
    [InlineData("KIMI_CODE_OAUTH_HOST")]
    [InlineData("KIMI_OAUTH_HOST")]
    public void AnyCodeHostOverridePreventsBorrowingCli(string key)
    {
        var selected = KimiAuthentication.Resolve("auto", () => null, () => throw new InvalidOperationException("Must not read CLI"),
            () => new("web", "web-token"), name => name == key ? "https://api.kimi.com" : null);
        Assert.Equal("web", selected!.Source);
    }
    [Theory]
    [InlineData("'synthetic-api'", "synthetic-api")]
    [InlineData("\"synthetic-api\"", "synthetic-api")]
    [InlineData("  \" synthetic-api \"  ", "synthetic-api")]
    [InlineData("\"unsafe header\"", null)]
    public void ApiKeysNormalizeBalancedOuterQuotes(string value, string? expected)
        => Assert.Equal(expected, KimiAuthentication.Resolve("api", () => value, () => null, () => null, _ => null)!.Token);
    [Fact]
    public void ExplicitSourcesNeverReadUnselectedCredentials()
    {
        KimiCredential? Unexpected() => throw new InvalidOperationException("Wrong source");
        Assert.Equal("api", KimiAuthentication.Resolve("api", () => "api", Unexpected, Unexpected, _ => null)!.Source);
        Assert.Equal("web", KimiAuthentication.Resolve("web", () => throw new InvalidOperationException(), Unexpected,
            () => new("web", "web"), _ => null)!.Source);
        Assert.Equal("cli", KimiAuthentication.Resolve("auto", () => null, () => new("cli", "cli"), Unexpected, _ => null)!.Source);
        Assert.Null(KimiAuthentication.Resolve("invalid", () => throw new InvalidOperationException(), Unexpected, Unexpected, _ => null));
    }
    [Theory]
    [InlineData("web-token", "web-token")]
    [InlineData("Cookie: other=unused; kimi-auth=web-token", "web-token")]
    [InlineData("'kimi-auth=web-token'", "web-token")]
    [InlineData("Bearer web-token", "web-token")]
    [InlineData("Cookie: 'kimi-auth=web-token'", "web-token")]
    [InlineData("Cookie: \"kimi-auth=web-token\"", "web-token")]
    [InlineData("kimi-auth=a; kimi-auth=b", null)]
    [InlineData("other=x", null)]
    [InlineData("kimi-auth=bad header", null)]
    public void WebOnlySelectsOneSafeNamedToken(string value, string? expected)
        => Assert.Equal(expected, KimiAuthentication.WebToken(value));
    [Fact]
    public void ReadCliLeavesTheCredentialAndAbsentDeviceFileUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "coderim-kimi-" + Guid.NewGuid()); if (root.StartsWith("/var/", StringComparison.Ordinal)) root = "/private" + root;
        Directory.CreateDirectory(Path.Combine(root, "credentials"));
        var file = Path.Combine(root, "credentials", "kimi-code.json"); var contents = """{"access_token":"fixture","refresh_token":"do-not-save","expires_at":1800001000}""";
        try
        {
            File.WriteAllText(file, contents);
            var selected = KimiAuthentication.ReadCli(root, key => key == "KIMI_CODE_HOME" ? root : null, Now);
            Assert.NotNull(selected); Assert.Equal("fixture", selected.Token); Assert.Equal(contents, File.ReadAllText(file));
            Assert.False(File.Exists(Path.Combine(root, "device_id")));
            Assert.DoesNotContain("do-not-save", JsonSerializer.Serialize(selected));
        }
        finally { Directory.Delete(root, true); }
    }
}
