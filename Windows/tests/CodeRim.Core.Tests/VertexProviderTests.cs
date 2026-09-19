using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;
public sealed class VertexProviderTests
{
    private static JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
    [Fact]
    public void VertexMatchesMetricLocationAndLimitWithoutCombiningUnrelatedQuotas()
    {
        var usage = Json("""{"timeSeries":[{"metric":{"labels":{"quota_metric":"requests","limit_name":"minute"}},"resource":{"labels":{"location":"a"}},"points":[{"value":{"int64Value":"25"}},{"value":{"doubleValue":30}}]},{"metric":{"labels":{"quota_metric":"requests"}},"resource":{"labels":{"location":"b"}},"points":[{"value":{"doubleValue":90}}]}]}""");
        var limits = Json("""{"timeSeries":[{"metric":{"labels":{"quota_metric":"requests","limit_name":"minute"}},"resource":{"labels":{"location":"a"}},"points":[{"value":{"int64Value":"100"}}]},{"metric":{"labels":{"quota_metric":"requests","limit_name":"minute"}},"resource":{"labels":{"location":"b"}},"points":[{"value":{"doubleValue":100}}]},{"metric":{"labels":{"quota_metric":"requests","limit_name":"day"}},"resource":{"labels":{"location":"b"}},"points":[{"value":{"doubleValue":1000}}]}]}""");
        var reading = NativeProviders.Parse("vertexai", new Dictionary<string, JsonElement> { ["usage"] = usage, ["limit"] = limits });
        Assert.Single(reading.Windows); Assert.Equal(30, reading.Headline!.UsedPercent);
    }
    [Fact]
    public async Task VertexUsesReadOnlyServiceAccountAssertionAndFollowsPages()
    {
        using var rsa = RSA.Create(2048); var oauthCalls = 0; var usageCalls = 0;
        var credential = JsonSerializer.Serialize(new { type = "service_account", client_email = "fixture@example.invalid", private_key = rsa.ExportPkcs8PrivateKeyPem(), project_id = "fixture-project" });
        using var provider = new NativeProviders(new Handler(async request =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                oauthCalls++; Assert.Null(request.Headers.Authorization);
                var form = (await request.Content!.ReadAsStringAsync()).Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
                Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", form["grant_type"]);
                var parts = form["assertion"].Split('.');
                static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
                using var payload = JsonDocument.Parse(Decode(parts[1]));
                Assert.Equal("https://oauth2.googleapis.com/token", payload.RootElement.GetProperty("aud").GetString());
                Assert.Equal("https://www.googleapis.com/auth/monitoring.read", payload.RootElement.GetProperty("scope").GetString());
                Assert.True(rsa.VerifyData(Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                return Ok("""{"access_token":"fixture-token","expires_in":3600}""");
            }
            Assert.Equal("Bearer fixture-token", request.Headers.Authorization!.ToString()); Assert.Contains("/fixture-project/timeSeries", request.RequestUri.AbsolutePath);
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (query.Contains("/allocation/usage", StringComparison.Ordinal))
            {
                usageCalls++;
                if (usageCalls == 1) return Ok("""{"timeSeries":[],"nextPageToken":"cursor+1"}""");
                Assert.Contains("pageToken=cursor+1", query);
                return Ok("""{"timeSeries":[{"metric":{"labels":{"quota_metric":"requests"}},"resource":{"labels":{}},"points":[{"value":{"doubleValue":25}}]}]}""");
            }
            return Ok("""{"timeSeries":[{"metric":{"labels":{"quota_metric":"requests"}},"resource":{"labels":{}},"points":[{"value":{"doubleValue":100}}]}]}""");
        }));
        var reading = await provider.FetchAsync("vertexai", credential, _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(1, oauthCalls); Assert.Equal(2, usageCalls); Assert.Equal(25, reading.Headline!.UsedPercent);
    }
    [Fact]
    public async Task VertexRejectsInvalidProjectBeforeSendingCredential()
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(_ => { calls++; return Task.FromResult(Ok("{}")); }));
        Assert.Equal(ReadingState.Error, (await provider.FetchAsync("vertexai", "fixture", _ => "../other", TestContext.Current.CancellationToken)).State);
        Assert.Equal(0, calls);
    }
    private static HttpResponseMessage Ok(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request); }
}
