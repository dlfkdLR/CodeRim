using System.Net;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;
public sealed class AzureProviderTests
{
    [Fact]
    public async Task PaidProbeIsOffUntilExplicitlyEnabled()
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler(_ => { calls++; return Task.FromResult(Ok("{}")); }));
        Assert.Equal(ReadingState.Disabled, (await provider.FetchAsync("azureopenai", "fixture", _ => null, TestContext.Current.CancellationToken)).State);
        Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData("v1", "/openai/v1/chat/completions", "max_completion_tokens", "64")]
    [InlineData("2024-10-21", "/openai/deployments/model/chat/completions", "max_tokens", "1")]
    public async Task AzureValidatesOnlyConfiguredDeploymentWithoutInventingQuota(string version, string path, string capName, string cap)
    {
        using var provider = new NativeProviders(new Handler(async request =>
        {
            Assert.Null(request.Headers.Authorization); Assert.Equal("fixture", request.Headers.GetValues("api-key").Single());
            Assert.Equal(path, request.RequestUri!.AbsolutePath); Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadAsStringAsync(); Assert.Contains("\"" + capName + "\":" + cap, body);
            return Ok("""{"model":"fixture-model","choices":[]}""");
        }));
        string? Setting(string key) => key switch {
            "AZURE_OPENAI_ENDPOINT" => "https://fixture.openai.azure.com/openai",
            "AZURE_OPENAI_DEPLOYMENT_NAME" => "model", "AZURE_OPENAI_API_VERSION" => version,
            "AZURE_OPENAI_ALLOW_BILLABLE_REQUESTS" => "true", _ => null };
        var reading = await provider.FetchAsync("azureopenai", "fixture", Setting, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Null(reading.Headline!.UsedPercent);
    }
    private static HttpResponseMessage Ok(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request); }
}
