using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
namespace CodeRim.Core.Tests;
public sealed class GoogleRegressionTests
{
    [Theory]
    [InlineData("token")]
    [InlineData("load")]
    [InlineData("quota")]
    public async Task ExplicitConsumerMigrationClearsOldQuotaAtEveryEndpoint(string stage)
    {
        using var provider = new NativeProviders(new Handler(request =>
        {
            var current = request.RequestUri!.Host == "oauth2.googleapis.com" ? "token" : request.RequestUri.AbsolutePath.EndsWith(":loadCodeAssist", StringComparison.Ordinal) ? "load" : "quota";
            if (stage == current) return new(stage == "token" ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden)
                { Content = new StringContent("""{"error":{"message":"Gemini Code Assist is no longer supported. Migrate to Antigravity."}}""") };
            return Ok(current == "token" ? """{"access_token":"fresh","expires_in":3600}""" : """{"cloudaicompanionProject":"fixture"}""");
        }));
        var reading = await provider.FetchAsync("gemini-cli", """{"refresh_token":"fixture","client_id":"client","client_secret":"secret"}""", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Unsupported, reading.State); Assert.Contains("consumer", reading.Message);
        Assert.Empty(ReadingRetention.Merge(reading, new("gemini-cli", ReadingState.Ready, [new("quota", "Quota", 25)])).Windows);
    }
    [Theory]
    [InlineData("", false, false, false, ReadingState.Unsupported)]
    [InlineData("free-tier", false, false, true, ReadingState.Unsupported)]
    [InlineData("standard-tier", false, false, true, ReadingState.NeedsAuth)]
    [InlineData("", true, false, false, ReadingState.Ready)]
    [InlineData("", false, true, false, ReadingState.Ready)]
    public async Task IneligibleConsumerTierDoesNotBlockLicensedOrWorkspaceAccounts(string tier, bool paid, bool workspace, bool quotaFails, ReadingState expected)
    {
        using var provider = new NativeProviders(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(":loadCodeAssist", StringComparison.Ordinal))
                return Ok(JsonSerializer.Serialize(new { currentTier = tier.Length > 0 ? new { id = tier } : null,
                    paidTier = paid ? new { name = "Code Assist Enterprise" } : null, cloudaicompanionProject = "fixture",
                    ineligibleTiers = new[] { new { reasonCode = "UNSUPPORTED_CLIENT" } } }));
            return quotaFails ? new(HttpStatusCode.Forbidden) { Content = new StringContent("""{"error":{"message":"SUBSCRIPTION_REQUIRED"}}""") }
                : Ok("""{"buckets":[{"modelId":"gemini-pro","remainingFraction":0.5}]}""");
        }));
        var credential = workspace ? JsonSerializer.Serialize(new { access_token = "access", expiry_date = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
            id_token = "header." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("""{"hd":"example.invalid"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature" }) : "access";
        Assert.Equal(expected, (await provider.FetchAsync("gemini-cli", credential, _ => null, TestContext.Current.CancellationToken)).State);
    }
    [Fact]
    public async Task MalformedServiceAccountKeyIsAReadingErrorNotAnUnhandledException()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler(_ => { calls++; return Ok("{}"); }));
        var reading = await provider.FetchAsync("vertexai", """{"type":"service_account","client_email":"fixture@example.invalid","private_key":"invalid PEM","project_id":"project"}""", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData("GCLOUD_PROJECT")]
    [InlineData("CLOUDSDK_CORE_PROJECT")]
    public async Task VertexAcceptsExistingProjectEnvironmentAliases(string variable)
    {
        using var provider = new NativeProviders(new Handler(request =>
        {
            Assert.Contains("/projects/target-project/", request.RequestUri!.AbsolutePath); return Ok("{}");
        }));
        await provider.FetchAsync("vertexai", "access", key => key == variable ? "target-project" : null, TestContext.Current.CancellationToken);
    }
    [Fact]
    public void ActiveGcloudProjectOutranksBillingProjectWithoutChangingCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "gcloud-fixture-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsMacOS() && (root.StartsWith("/var/", StringComparison.Ordinal) || root.StartsWith("/tmp/", StringComparison.Ordinal))) root = "/private" + root;
        Directory.CreateDirectory(Path.Combine(root, "configurations"));
        try
        {
            var path = Path.Combine(root, "application_default_credentials.json");
            const string raw = """{"type":"authorized_user","quota_project_id":"billing-project","refresh_token":"fixture"}""";
            File.WriteAllText(path, raw); File.WriteAllText(Path.Combine(root, "active_config"), "work");
            File.WriteAllText(Path.Combine(root, "configurations", "config_work"), "[core]\nproject = target-project\n");
            using var auth = JsonDocument.Parse(GoogleAuthentication.Read(path, root));
            Assert.Equal("target-project", auth.RootElement.GetProperty("coderim_project").GetString()); Assert.Equal(raw, File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }
    private static HttpResponseMessage Ok(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request)); }
}
