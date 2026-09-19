using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
namespace CodeRim.Core.Tests;
public sealed class GeminiProviderTests
{
    [Fact]
    public void InstalledCliClientMetadataIsParsedWithoutExecutingJavaScript()
    {
        var credential = GeminiAuthentication.BuildCredential("""{"access_token":"fixture","expiry_date":1}""",
            ["const OAUTH_CLIENT_ID = 'fixture.apps.googleusercontent.com'; const OAUTH_CLIENT_SECRET = \"fixture-secret\"; throw new Error('never execute');"]);
        using var document = JsonDocument.Parse(credential);
        Assert.Equal("fixture.apps.googleusercontent.com", document.RootElement.GetProperty("client_id").GetString());
        Assert.Equal("fixture-secret", document.RootElement.GetProperty("client_secret").GetString());
        Assert.DoesNotContain("never execute", credential);
        Assert.Throws<InvalidDataException>(() => GeminiAuthentication.BuildCredential("[]", []));
    }
    [Fact]
    public async Task ExpiredOAuthRefreshIsAccountScopedAndQuotaUsesManagedProject()
    {
        var refreshes = 0;
        using var provider = new NativeProviders(new Handler(async request =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                refreshes++; Assert.Null(request.Headers.Authorization);
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("grant_type=refresh_token", body); Assert.Contains("client_id=client", body);
                return Ok("""{"access_token":"fresh","expires_in":3600}""");
            }
            Assert.Equal("Bearer fresh", request.Headers.Authorization!.ToString());
            if (request.RequestUri.AbsolutePath.EndsWith(":loadCodeAssist", StringComparison.Ordinal))
                return Ok("""{"cloudaicompanionProject":{"id":"managed"},"currentTier":{"name":"Code Assist Standard"}}""");
            Assert.Equal("""{"project":"managed"}""", await request.Content!.ReadAsStringAsync());
            return Ok("""{"buckets":[{"modelId":"gemini-pro","remainingFraction":0.9},{"modelId":"gemini-pro","remainingFraction":0.25,"resetTime":"2026-09-20T00:00:00Z"},{"modelId":"gemini-flash","remainingFraction":0.5}]}""");
        }));
        const string credential = """{"access_token":"expired","expiry_date":1,"refresh_token":"account-a","client_id":"client","client_secret":"secret"}""";
        var first = await provider.FetchAsync("gemini-cli", credential, _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(75, first.Headline!.UsedPercent); Assert.Equal(2, first.Windows.Count); Assert.Equal("Code Assist Standard", first.Plan);
        _ = await provider.FetchAsync("gemini-cli", credential, _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(1, refreshes);
        _ = await provider.FetchAsync("gemini-cli", credential.Replace("account-a", "account-b", StringComparison.Ordinal), _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(2, refreshes);
    }
    [Fact]
    public async Task ExpiredLoginWithoutCliClientCannotSendExpiredCredential()
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(_ => { calls++; return Task.FromResult(Ok("{}")); }));
        var reading = await provider.FetchAsync("gemini-cli", """{"access_token":"expired","expiry_date":1}""", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, reading.State); Assert.Equal(0, calls);
        Assert.DoesNotContain("GEMINI_API_KEY", NativeProviders.CredentialKeys("gemini-cli")!);
    }

    [Fact]
    public void WindowsBundledCliAndJsoncSettingsCanSupplyRefreshMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "coderim-gemini-" + Guid.NewGuid().ToString("N"));
        // GuardedFile intentionally refuses linked parents; use the canonical temp root on macOS.
        if (OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal)) root = "/private" + root;
        if (OperatingSystem.IsMacOS() && root.StartsWith("/tmp/", StringComparison.Ordinal)) root = "/private" + root;
        Directory.CreateDirectory(Path.Combine(root, ".gemini"));
        var npm = Path.Combine(root, "npm"); var bundle = Path.Combine(npm, "node_modules", "@google", "gemini-cli", "bundle"); Directory.CreateDirectory(bundle);
        try
        {
            File.WriteAllText(Path.Combine(root, ".gemini", "settings.json"), """
                { // CLI accepts comments
                "security":{"auth":{"selectedType":"oauth-personal",}},
                }
                """);
            File.WriteAllText(Path.Combine(root, ".gemini", "oauth_creds.json"), """{"refresh_token":"fixture","expiry_date":1}""");
            File.WriteAllText(Path.Combine(bundle, "chunk-fixture.js"), new string(' ', 3 * 1024 * 1024) + "var OAUTH_CLIENT_ID='fixture.apps.googleusercontent.com';var OAUTH_CLIENT_SECRET='fixture-secret';");
            using var first = JsonDocument.Parse(GeminiAuthentication.Read(root, [npm])!);
            Assert.Equal("fixture-secret", first.RootElement.GetProperty("client_secret").GetString());
            using var cached = JsonDocument.Parse(GeminiAuthentication.Read(root, [npm])!);
            Assert.Equal("fixture", cached.RootElement.GetProperty("refresh_token").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("gemini-cli")]
    [InlineData("vertexai")]
    public async Task RevokedRefreshTokenClearsOldAccountQuota(string id)
    {
        using var provider = new NativeProviders(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("""{"error":"invalid_grant","error_description":"Do not expose this response"}""") })));
        var reading = await provider.FetchAsync(id, """{"refresh_token":"revoked","client_id":"client","client_secret":"secret"}""", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.NeedsAuth, reading.State);
        var previous = new ProviderReading(id, ReadingState.Ready, [new("quota", "Quota", 50)]);
        Assert.Empty(ReadingRetention.Merge(reading, previous).Windows);
        Assert.DoesNotContain("Do not expose", reading.Message);
    }
    [Fact]
    public async Task TemporaryRefreshFailureRetainsLastQuota()
    {
        using var provider = new NativeProviders(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("temporarily unavailable") })));
        var reading = await provider.FetchAsync("gemini-cli", """{"refresh_token":"fixture","client_id":"client","client_secret":"secret"}""", _ => null, TestContext.Current.CancellationToken);
        var previous = new ProviderReading("gemini-cli", ReadingState.Ready, [new("quota", "Quota", 50)]);
        Assert.Equal(ReadingState.Stale, ReadingRetention.Merge(reading, previous).State);
    }

    private static HttpResponseMessage Ok(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request); }
}
