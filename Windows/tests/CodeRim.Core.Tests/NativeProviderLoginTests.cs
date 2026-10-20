using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class NativeProviderLoginTests
{
    [Fact]
    public void CommandCodeEnvironmentCredentialUsesTheReferenceKeyRatherThanTheCatalogueUrl()
    {
        var keys = NativeProviders.CredentialKeys("commandcode");
        Assert.NotNull(keys); Assert.Equal(["COMMAND_CODE_API_KEY"], keys);
    }

    [Theory]
    [InlineData("cursor", "Cursor", "pro", "https://cursor.com/dashboard")]
    [InlineData("grok", "Grok", null, "https://grok.com/?_s=usage")]
    [InlineData("commandcode", "Command Code", "GOAT", "https://commandcode.ai/")]
    [InlineData("ollama", "Ollama", null, "https://ollama.com/settings")]
    [InlineData("opencode", "OpenCode", "Go", "https://opencode.ai/")]
    public void NativeReadingsRetainReferenceSourcePlanAndFixedDestination(string id, string source, string? plan, string link)
    {
        var root = JsonSerializer.SerializeToElement(new
        {
            usage = new { rolling = new { percent = 25 } }, limits = new { monthly = new { usage = .25 } },
            config = new { creditUsagePercent = 25 }, individualUsage = new { plan = new { autoPercentUsed = 25 } }, membershipType = "pro",
            credits = new { monthlyCredits = 75 }, totalCost = 25, data = new { planId = "goat_monthly" },
            identity = new { accountEmail = "unrelated@example.invalid", manageURL = "file:///private", accessToken = "never-display" }
        });
        var reading = NativeProviders.Parse(id, new Dictionary<string, JsonElement>
            { ["main"] = root, ["credits"] = root, ["subscription"] = root, ["usage"] = root });
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal(25, Assert.Single(reading.Windows).UsedPercent);
        Assert.Equal(new ProviderAccountMetadata(null, source), reading.Account);
        Assert.Equal(plan, reading.Plan); Assert.Equal(link, ProviderAccountLinks.UsagePage(id)!.AbsoluteUri);
        Assert.DoesNotContain("never-display", JsonSerializer.Serialize(reading), StringComparison.Ordinal);
        Assert.DoesNotContain("unrelated@example.invalid", JsonSerializer.Serialize(reading), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("member@example.invalid", "member@example.invalid")]
    [InlineData("  테스트 🧪  ", "테스트 🧪")]
    [InlineData("unsafe\nname", null)]
    [InlineData("", null)]
    public void CommandCodeKeepsOnlyBoundedDisplayNameWithItsCredential(string? name, string? expected)
    {
        var login = NativeProviderLogin.CommandCode(JsonSerializer.SerializeToElement(new { apiKey = "fixture-secret", userName = name }));
        Assert.NotNull(login); Assert.Equal("fixture-secret", login.Credential);
        Assert.Equal(expected, login.Account.Label); Assert.Equal("Command Code", login.Account.Source);
        Assert.DoesNotContain("fixture-secret", login.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(login), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("invalid\r\nvalue")]
    public void MissingOrMalformedCredentialCannotPublishAnIdentity(string? key)
        => Assert.Null(NativeProviderLogin.CommandCode(JsonSerializer.SerializeToElement(new { apiKey = key, userName = "owner" })));

    [Fact]
    public void GrokIdentityComesFromTheSameLiveTrustedIssuerEntry()
    {
        using var json = JsonDocument.Parse("""
          {"https://private.invalid":{"key":"private-secret","email":"private@example.invalid"},
           "https://auth.x.ai::old":{"key":"expired-secret","expires_at":"2020-01-01T00:00:00Z","email":"old@example.invalid"},
           "https://auth.x.ai::new":{"key":"live-secret","expires_at":"2030-01-01T00:00:00Z","email":"new@example.invalid"}}
          """);
        var login = NativeProviderLogin.Grok(json.RootElement, new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        Assert.NotNull(login); Assert.Equal("live-secret", login.Credential); Assert.Equal("new@example.invalid", login.Account.Label);
        Assert.Null(NativeProviderLogin.Grok(json.RootElement, new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(login), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://auth.x.ai", true)] [InlineData("https://auth.x.ai::client", true)]
    [InlineData("https://auth.x.ai.attacker.invalid", false)] [InlineData("https://auth.x.ai/", false)]
    public void GrokIssuerMatchingDoesNotBroadenTheCredentialDestination(string issuer, bool accepted)
    {
        var root = JsonSerializer.SerializeToElement(new Dictionary<string, object> { [issuer] = new { key = "fixture-secret", email = "fixture@example.invalid" } });
        Assert.Equal(accepted, NativeProviderLogin.Grok(root, DateTimeOffset.UtcNow) is not null);
    }

    private static Dictionary<string, byte[]> CursorState(string? subject = "editor-owner") => new()
    {
        ["cursorAuth/accessToken"] = Encoding.UTF8.GetBytes("fixture-token"),
        ["cursorAuth/stripeMembershipAuthId"] = Encoding.UTF8.GetBytes(subject ?? ""),
        ["cursorAuth/cachedEmail"] = Encoding.UTF8.GetBytes("editor@example.invalid"),
        ["cursorAuth/stripeMembershipType"] = Encoding.UTF8.GetBytes("pro")
    };

    [Fact]
    public void CursorUsesOneEditorSnapshotAndCanFallBackToTheTokenSubject()
    {
        var values = CursorState(); var login = NativeProviderLogin.Cursor(values);
        Assert.NotNull(login); Assert.Equal("WorkosCursorSessionToken=editor-owner::fixture-token", login.Credential);
        Assert.Equal("editor@example.invalid", login.Account.Label); Assert.Equal("pro", login.Plan);
        values["cursorAuth/stripeMembershipAuthId"] = [];
        values["cursorAuth/accessToken"] = Encoding.UTF8.GetBytes("header." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"sub\":\"jwt-owner\"}")).TrimEnd('=') + ".signature");
        Assert.StartsWith("WorkosCursorSessionToken=jwt-owner::", NativeProviderLogin.Cursor(values)!.Credential, StringComparison.Ordinal);
        values["cursorAuth/cachedEmail"] = [0xff];
        Assert.Null(NativeProviderLogin.Cursor(values)!.Account.Label);
    }

    [Theory]
    [InlineData("owner; other=cookie")] [InlineData("owner\r\nheader")] [InlineData("owner::token")]
    public void CursorRejectsAnAmbiguousCookieSubject(string subject) => Assert.Null(NativeProviderLogin.Cursor(CursorState(subject)));

    [Fact]
    public void InvalidOptionalMetadataDoesNotDiscardValidQuotaCredentials()
    {
        var values = CursorState(); values["cursorAuth/cachedEmail"] = Encoding.UTF8.GetBytes(new string('a', 257));
        values["cursorAuth/stripeMembershipType"] = Encoding.UTF8.GetBytes("invalid\nplan");
        var login = NativeProviderLogin.Cursor(values);
        Assert.NotNull(login); Assert.Null(login.Account.Label); Assert.Null(login.Plan);
        values["cursorAuth/accessToken"] = Encoding.UTF8.GetBytes(new string('a', 65537));
        Assert.Null(NativeProviderLogin.Cursor(values));
    }
}
