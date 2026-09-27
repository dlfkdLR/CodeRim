using System.Text;
using System.Text.Json;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class NativeAccountSummaryTests
{
    private static Dictionary<string, byte[]> CursorState() => new()
    {
        ["cursorAuth/cachedEmail"] = Encoding.UTF8.GetBytes("editor@example.invalid"),
        ["cursorAuth/stripeMembershipType"] = Encoding.UTF8.GetBytes("pro")
    };

    [Fact]
    public void CursorCanDescribeAnEditorWithoutAuthorizingARequest()
    {
        var values = CursorState(); var summary = NativeAccountSummary.Cursor(values);
        Assert.Equal("editor@example.invalid", summary?.Account?.Label); Assert.Equal("pro", summary?.Plan);
        Assert.Null(NativeProviderLogin.Cursor(values));
        values["cursorAuth/cachedEmail"] = [0xff];
        Assert.Null(NativeAccountSummary.Cursor(values));
    }

    [Fact]
    public void CursorWithoutEmailHasQuotaOwnershipButNoEditorAccountSection()
    {
        var values = new Dictionary<string, byte[]>
        {
            ["cursorAuth/accessToken"] = Encoding.UTF8.GetBytes("fixture-token"),
            ["cursorAuth/stripeMembershipAuthId"] = Encoding.UTF8.GetBytes("fixture-owner")
        };
        var summary = NativeAccountSummary.Cursor(values);
        Assert.NotNull(NativeProviderLogin.Cursor(values)); Assert.NotNull(summary); Assert.Null(summary.Account); Assert.Null(summary.Plan);
        values["cursorAuth/accessToken"] = Encoding.UTF8.GetBytes("replacement-token");
        Assert.NotEqual(summary.Version, NativeAccountSummary.Cursor(values)?.Version);
    }

    [Fact]
    public void CursorMetadataChangeInvalidatesDisplayOwnerEvenWithTheSameCredential()
    {
        var values = CursorState(); var first = NativeAccountSummary.Cursor(values)!;
        values["cursorAuth/cachedEmail"] = Encoding.UTF8.GetBytes("other@example.invalid");
        Assert.NotEqual(first.Version, NativeAccountSummary.Cursor(values)!.Version);
        values["cursorAuth/cachedEmail"] = Encoding.UTF8.GetBytes(new string('x', 257));
        Assert.Null(NativeAccountSummary.Cursor(values));
    }

    [Fact]
    public void ExpiredGrokAccountRemainsDisplayOnlyAndNeverChoosesAnUntrustedIssuer()
    {
        var now = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        using var json = JsonDocument.Parse("""
            {"https://auth.x.ai.attacker.invalid":{"key":"untrusted-secret","email":"wrong@example.invalid"},
             "https://auth.x.ai::old":{"key":"expired-secret","expires_at":"2020-01-01T00:00:00Z","email":"old@example.invalid"},
             "https://auth.x.ai::live":{"key":"live-secret","expires_at":"2030-01-01T00:00:00Z","email":"live@example.invalid"}}
            """);
        Assert.Equal("live@example.invalid", NativeAccountSummary.Grok(json.RootElement, now)?.Account?.Label);
        var expired = NativeAccountSummary.Grok(json.RootElement, now.AddYears(10))!;
        Assert.Equal("old@example.invalid", expired.Account?.Label);
        Assert.Null(NativeProviderLogin.Grok(json.RootElement, now.AddYears(10)));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(expired), StringComparison.Ordinal);
        Assert.DoesNotContain(expired.Version, JsonSerializer.Serialize(expired), StringComparison.Ordinal);
        Assert.DoesNotContain("old@example.invalid", expired.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://auth.x.ai.attacker.invalid", "key")]
    [InlineData("https://auth.x.ai", "")]
    [InlineData("https://auth.x.ai", "bad\r\nkey")]
    public void InvalidGrokSourceDoesNotCreateAnAccount(string issuer, string key)
        => Assert.Null(NativeAccountSummary.Grok(JsonSerializer.SerializeToElement(new Dictionary<string, object>
            { [issuer] = new { key, email = "fixture@example.invalid" } }), DateTimeOffset.UtcNow));

    [Fact]
    public void ExpiredCredentialsAndChosenSourceHaveIndependentVersions()
    {
        static NativeAccountSummary Summary(string key) => NativeAccountSummary.Grok(JsonSerializer.SerializeToElement(
            new Dictionary<string, object> { ["https://auth.x.ai"] = new { key, expires_at = "2020-01-01T00:00:00Z", email = "same@example.invalid" } }), DateTimeOffset.UtcNow)!;
        Assert.NotEqual(Summary("a").Version, Summary("b").Version);
        Assert.NotEqual(NativeAccountSummary.FromCredential("cursor", "same", "key")!.Version,
            NativeAccountSummary.FromCredential("cursor", "same", "browser")!.Version);
        Assert.Equal("Go", NativeAccountSummary.FromCredential("opencode", "fixture")!.Plan);
        Assert.Null(NativeAccountSummary.FromCredential("opencode", " "));
    }

    [Fact]
    public void GlmDisplayRetainsSourceAndRegionButNotTheCredentialOrAnUnverifiedPlan()
    {
        var local = NativeAccountSummary.Glm(new("fixture-secret", "bigmodel-cn", "OpenCode"))!;
        Assert.Equal("OpenCode", local.Account?.Source); Assert.Equal("bigmodel-cn", local.Account?.Region);
        Assert.Null(local.Account?.Label); Assert.Null(local.Plan);
        Assert.Equal("https://open.bigmodel.cn/usage", CodeRim.Core.Domain.ProviderAccountLinks.UsagePage("glm", local.Account?.Region)?.AbsoluteUri);
        var selected = NativeAccountSummary.Glm(new("fixture-secret", "global", "api"))!;
        Assert.Equal("api", selected.Account?.Source); Assert.Equal("global", selected.Account?.Region);
        Assert.NotEqual(local.Version, selected.Version);
        Assert.NotEqual(local.Version, NativeAccountSummary.Glm(new("replacement", "bigmodel-cn", "OpenCode"))!.Version);
        Assert.NotEqual(local.Version, NativeAccountSummary.Glm(new("fixture-secret", "global", "OpenCode"))!.Version);
        Assert.NotEqual(local.Version, NativeAccountSummary.Glm(new("fixture-secret", "bigmodel-cn", "Claude Code"))!.Version);
        var json = JsonSerializer.Serialize(local);
        Assert.DoesNotContain("fixture-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain(local.Version, json, StringComparison.Ordinal);
        Assert.Equal("Detected provider connection", local.ToString());
    }

    [Theory]
    [InlineData("", "global", "api")]
    [InlineData("bad\nkey", "global", "api")]
    [InlineData("key", "unsupported", "api")]
    [InlineData("key", "https://untrusted.invalid", "api")]
    [InlineData("key", "global", "untrusted")]
    public void GlmDisplayRejectsInvalidCredentialsRegionsAndSources(string token, string region, string source)
        => Assert.Null(NativeAccountSummary.Glm(new(token, region, source)));
}
