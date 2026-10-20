using System.Text.Json;
using System.Net;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class ProviderAccountMetadataTests
{
    [Theory]
    [InlineData("poe", "api")]
    [InlineData("openrouter", "api")]
    [InlineData("perplexity", "web")]
    [InlineData("t3chat", "web")]
    public void ScriptAccountIdentityTravelsWithItsReading(string id, string source)
    {
        using var response = JsonDocument.Parse("""
            {"identity":{"accountEmail":"fixture@example.invalid","loginMethod":"pro"},
             "primary":{"usedPercent":25,"windowMinutes":300}}
            """);
        var reading = ScriptProviders.Map(id, response.RootElement);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(reading, CompanionFile.JsonOptions));
        Assert.True(json.RootElement.TryGetProperty("account", out var account), "The provider reading discards its account metadata.");
        Assert.Equal("fixture@example.invalid", account.GetProperty("label").GetString());
        Assert.Equal(source, account.GetProperty("source").GetString());
        Assert.Equal("pro", reading.Plan);
        Assert.Equal(25, Assert.Single(reading.Windows).UsedPercent);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")] [InlineData("a\nb")]
    [InlineData("a\u0000b")]
    public void InvalidIdentityIsOmittedWithoutDiscardingQuota(string? label)
    {
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(new { identity = new { accountEmail = label }, primary = new { usedPercent = 42 } }));
        var reading = ScriptProviders.Map("poe", response.RootElement);
        Assert.Null(reading.Account!.Label); Assert.Equal("api", reading.Account.Source);
        Assert.Equal(42, Assert.Single(reading.Windows).UsedPercent);
    }

    [Fact]
    public void DisplayLimitsUseUtf8AndPreserveUnicode()
    {
        Assert.Equal("테스트 🧪", ProviderAccountMetadata.DisplayText("  테스트 🧪  "));
        Assert.Equal(new string('é', 128), ProviderAccountMetadata.DisplayText(new string('é', 128)));
        Assert.Null(ProviderAccountMetadata.DisplayText(new string('é', 129)));
        Assert.Null(ProviderAccountMetadata.DisplayText(new string('a', 257)));
    }

    [Theory]
    [InlineData("poe", "https://poe.com/api/keys")]
    [InlineData("perplexity", "https://www.perplexity.ai/account/usage")]
    [InlineData("copilot", "https://github.com/settings/copilot")]
    [InlineData("sub2api", null)] [InlineData("synthetic", null)] [InlineData("unknown", null)]
    public void ManagementLinksComeOnlyFromReferenceDescriptors(string id, string? expected)
    {
        using var response = JsonDocument.Parse("""{"identity":{"manageURL":"file:///private","source":"response-secret"},"primary":{"usedPercent":42}}""");
        var reading = ScriptProviders.Map(id, response.RootElement);
        Assert.Equal(expected, ProviderAccountLinks.UsagePage(id)?.AbsoluteUri);
        Assert.DoesNotContain("response-secret", JsonSerializer.Serialize(reading), StringComparison.Ordinal);
        Assert.DoesNotContain("file:///private", JsonSerializer.Serialize(reading), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200)] [InlineData(401)] [InlineData(503)]
    public async Task RealScriptTransportCapturesWebSourceWithoutPersistingCookie(int status)
    {
        using var providers = new ScriptProviders(new Handler(status));
        var reading = await providers.FetchAsync("perplexity", _ => null, "session=fixture-private-cookie", TestContext.Current.CancellationToken);
        if (status == 200)
        {
            Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal("web", reading.Account!.Source);
            Assert.Equal("Pro", reading.Plan); Assert.Null(reading.Account.Label);
            Assert.Equal(25, reading.Windows[0].UsedPercent);
        }
        else { Assert.Null(reading.Account); Assert.Empty(reading.Windows); }
        Assert.DoesNotContain("fixture-private-cookie", JsonSerializer.Serialize(reading), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReadingState.Error, true)] [InlineData(ReadingState.Unavailable, true)]
    [InlineData(ReadingState.NeedsAuth, false)] [InlineData(ReadingState.Disabled, false)]
    public void TransientRetentionKeepsOneIdentityAndAuthInvalidationRemovesIt(ReadingState state, bool retained)
    {
        var previous = new ProviderReading("poe", ReadingState.Ready, [new("quota", "Quota", 42)],
            DateTimeOffset.Now, Plan: "Pro", Account: new("old@example.invalid", "api"));
        var result = ReadingRetention.Merge(new("poe", state, []), previous);
        Assert.Equal(retained ? previous.Account : null, result.Account);
        Assert.Equal(retained ? previous.Plan : null, result.Plan);
        if (retained) Assert.Equal(ReadingState.Stale, result.State);
    }

    [Fact]
    public void MetadataRoundTripAndLegacyAbsenceAreCompatible()
    {
        var now = DateTimeOffset.Now; var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var reading = new ProviderReading("poe", ReadingState.Ready, [new("quota", "Quota", 42)], now,
            Account: new("테스트@example.invalid", "api"));
        try
        {
            CompanionFile.Write(new(1, now, [new("poe", "Poe", true, null, reading, "fixture-scope")]), path);
            Assert.Equal(reading.Account, Assert.Single(CompanionFile.Read(path).Providers).Limits.Account);
            var legacy = reading with { Account = null };
            var json = JsonSerializer.Serialize(legacy, CompanionFile.JsonOptions);
            Assert.DoesNotContain("account", json, StringComparison.Ordinal);
            Assert.Null(JsonSerializer.Deserialize<ProviderReading>(json, CompanionFile.JsonOptions)!.Account);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(null, "")] [InlineData(null, "api\nsecret")] [InlineData(" ", "api")]
    [InlineData("a\nb", "api")] [InlineData(null, null)]
    public void SnapshotRejectsMalformedAccountMetadata(string? label, string? source)
    {
        var now = DateTimeOffset.Now; var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var reading = new ProviderReading("poe", ReadingState.Ready, [], now, Account: new(label, source!));
        try
        {
            Assert.Throws<InvalidDataException>(() => CompanionFile.Write(new(1, now, [new("poe", "Poe", true, null, reading)]), path));
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("github.com:\n  user: fixture-a\n  oauth_token: fixture-token", "fixture-token", "fixture-a")]
    [InlineData("github.com:\n  user: fixture-b\n  oauth_token: other-token", "fixture-token", null)]
    [InlineData("github.com:\n  user: fixture-a", "fixture-token", null)]
    [InlineData("github.com:\n  user: fixture-a\n  users:\n    fixture-a:\n      oauth_token: fixture-token", "fixture-token", "fixture-a")]
    [InlineData("github.com:\n  user: fixture-a\n  oauth_token: other-token\n  users:\n    fixture-a:\n      oauth_token: fixture-token", "fixture-token", null)]
    [InlineData("github.com:\n  user: fixture-a\n  user: fixture-b\n  oauth_token: fixture-token", "fixture-token", null)]
    public void GitHubDisplayIdentityMustBelongToTheSelectedToken(string hosts, string token, string? expected)
        => Assert.Equal(expected, GitHubAuthentication.AccountLabel(hosts, token));

    [Theory]
    [InlineData(null, "https://z.ai/manage-apikey/apikey-list")]
    [InlineData("global", "https://z.ai/manage-apikey/apikey-list")]
    [InlineData("bigmodel-cn", "https://open.bigmodel.cn/usage")]
    public void NativeGlmUsesTheReferenceAccountPageForItsCapturedRegion(string? region, string expected)
        => Assert.Equal(expected, ProviderAccountLinks.UsagePage("glm", region)!.AbsoluteUri);

    [Fact]
    public void AccountRegionCannotCarryAResponseSuppliedAddress()
        => Assert.Throws<InvalidDataException>(() => new ProviderAccountMetadata(null, "api", "https://untrusted.invalid").Validate());

    private sealed class Handler(int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("""
                {"credit_grants":[{"type":"recurring","amount_cents":2000}],"total_usage_cents":500,"renewal_date_ts":2000000000}
                """) });
    }
}
