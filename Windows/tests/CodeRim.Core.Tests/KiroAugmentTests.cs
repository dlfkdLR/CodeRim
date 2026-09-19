using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;
namespace CodeRim.Core.Tests;
public sealed class KiroAugmentTests
{
    [Fact]
    public async Task KiroProfileRoutesRegionAndSeparatesOverage()
    {
        using var provider = new NativeProviders(new Handler(async request =>
        {
            Assert.Equal("https://q.eu-central-1.amazonaws.com/", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer access", request.Headers.Authorization!.ToString());
            Assert.Equal("AmazonCodeWhispererService.GetUsageLimits", Assert.Single(request.Headers.GetValues("X-Amz-Target")));
            Assert.Contains("arn:aws:codewhisperer:eu-central-1:123:profile/example", await request.Content!.ReadAsStringAsync());
            return Ok("""{"nextDateReset":1800000000,"overageConfiguration":{"overageStatus":"ENABLED"},"usageBreakdownList":[{"resourceType":"CREDIT","currentUsageWithPrecision":120,"usageLimitWithPrecision":100,"currentOveragesWithPrecision":20,"overageCapWithPrecision":80,"overageCharges":4,"currency":"EUR"}]}""");
        }));
        var reading = await provider.FetchAsync("kiro", """{"access_token":"access","profileArn":"arn:aws:codewhisperer:eu-central-1:123:profile/example"}""", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal(100, reading.Windows[0].UsedPercent);
        Assert.Equal(25, reading.Windows[1].UsedPercent); Assert.Equal("EUR", reading.Windows[2].Unit);
    }
    [Fact]
    public async Task InvalidKiroRegionNeverSendsToken()
    {
        var calls = 0; using var provider = new NativeProviders(new Handler(_ => { calls++; return Task.FromResult(Ok("{}")); }));
        var reading = await provider.FetchAsync("kiro", "access", _ => "arn:aws:codewhisperer:evil.example:123:profile/example", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State); Assert.Equal(0, calls);
    }
    [Fact]
    public void BonusUsageDoesNotInventAnAllowance()
    {
        using var payload = JsonDocument.Parse("""{"nextDateReset":1800000000,"usageBreakdownList":[{"resourceType":"CREDIT","currentUsageWithPrecision":150,"usageLimitWithPrecision":100,"bonuses":[{}]}]}""");
        var reading = NativeProviders.Parse("kiro", new Dictionary<string, JsonElement> { ["main"] = payload.RootElement });
        Assert.Null(reading.Headline!.UsedPercent); Assert.Contains("Plan and bonus", reading.Headline.Name);
    }
    [Fact]
    public void KiroDatabaseIsReadWithoutChangingCliState()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiro-test-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsMacOS() && (root.StartsWith("/var/", StringComparison.Ordinal) || root.StartsWith("/tmp/", StringComparison.Ordinal))) root = "/private" + root;
        Directory.CreateDirectory(root); var path = Path.Combine(root, "data.sqlite3");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open(); using var cmd = connection.CreateCommand();
                cmd.CommandText = """CREATE TABLE auth_kv(key TEXT,value TEXT); CREATE TABLE state(key TEXT,value TEXT); INSERT INTO auth_kv VALUES('kirocli:odic:token','{"access_token":"fixture"}'); INSERT INTO state VALUES('api.codewhisperer.profile','{"arn":"arn:aws:codewhisperer:us-east-1:123:profile/example"}');""";
                cmd.ExecuteNonQuery();
            }
            var before = File.ReadAllBytes(path); using var auth = JsonDocument.Parse(KiroAuthentication.Read(path)!);
            Assert.Equal("fixture", auth.RootElement.GetProperty("access_token").GetString()); Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task AugmentReadsWebCreditsAndOptionalBillingCycle()
    {
        using var provider = new NativeProviders(new Handler(request =>
        {
            Assert.Null(request.Headers.Authorization); Assert.Equal("session=fixture", Assert.Single(request.Headers.GetValues("Cookie")));
            return Task.FromResult(Ok(request.RequestUri!.AbsolutePath == "/api/credits" ? """{"usageUnitsRemaining":800,"usageUnitsConsumedThisBillingCycle":200,"usageUnitsAvailable":1000}"""
                : """{"planName":"Max","billingPeriodEnd":"2026-10-01T00:00:00Z"}"""));
        }));
        var reading = await provider.FetchAsync("augment", "Cookie: session=fixture", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(20, reading.Headline!.UsedPercent); Assert.Equal("Max", reading.Plan); Assert.Equal("credits", reading.Headline.Unit);
    }
    private static HttpResponseMessage Ok(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request); }
}
