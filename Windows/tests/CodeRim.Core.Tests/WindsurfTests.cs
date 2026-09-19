using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;
public sealed class WindsurfTests
{
    [Fact]
    public async Task BinaryPlanStatusPreservesZeroRemainingAndUsesDevinSession()
    {
        using var provider = new NativeProviders(new Handler(async request =>
        {
            Assert.Null(request.Headers.Authorization); Assert.Equal("session", request.Headers.GetValues("x-devin-session-token").Single());
            Assert.Equal("auth1", request.Headers.GetValues("x-devin-auth1-token").Single());
            Assert.Equal("application/proto", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(new byte[] { 10, 7, 115, 101, 115, 115, 105, 111, 110, 16, 1 }, await request.Content.ReadAsByteArrayAsync());
            // Response.plan_status { plan_info { name:"Pro" }, daily:0, weekly:75 }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([10, 11, 10, 5, 18, 3, 80, 114, 111, 112, 0, 120, 75]) };
        }));
        var reading = await provider.FetchAsync("windsurf", """{"devin_session_token":"session","devin_auth1_token":"auth1","devin_account_id":"account","devin_primary_org_id":"org"}""", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal("Pro", reading.Plan);
        Assert.Equal(100, reading.Windows[0].UsedPercent); Assert.Equal(25, reading.Windows[1].UsedPercent);
    }
    [Theory]
    [InlineData("CgQKAhIB")] // malformed nested plan
    [InlineData("CgNwgIA=")] // unterminated varint
    [InlineData("AA==")] // zero field key
    public async Task MalformedBinaryReturnsErrorWithoutEscaping(string encoded)
    {
        using var provider = new NativeProviders(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String(encoded)) })));
        var reading = await provider.FetchAsync("windsurf", """{"sessionToken":"s","auth1Token":"a","accountID":"id","primaryOrgID":"org"}""", _ => null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, reading.State);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request); }
}
