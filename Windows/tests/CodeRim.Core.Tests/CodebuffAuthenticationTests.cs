using System.Net;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
namespace CodeRim.Core.Tests;

public sealed class CodebuffAuthenticationTests
{
    [Theory]
    [InlineData("""{"default":{"authToken":" 'local' "},"authToken":"root"}""", "local")]
    [InlineData("""{"default":{"authToken":"  "},"authToken":"root"}""", "root")]
    [InlineData("""{"authToken":"opaque-token"}""", "opaque-token")]
    [InlineData("""{"default":null,"authToken":"root"}""", "root")]
    [InlineData("""{"default":{"authToken":"a","authToken":"b"}}""", null)]
    [InlineData("""{"authToken":"a","authToken":"b"}""", null)]
    [InlineData("""{"default":[],"authToken":"root"}""", null)]
    [InlineData("""{"authToken":false}""", null)]
    [InlineData("""{"authToken":"line\nbreak"}""", null)]
    [InlineData("""{"default":{"authToken":123},"authToken":"root"}""", null)]
    [InlineData("""{"default":{"authToken":[]},"authToken":"root"}""", null)]
    [InlineData("""{"default":{"authToken":"local"},"authToken":123}""", null)]
    [InlineData("""{"default":{"authToken":"local"},"authToken":[]}""", null)]
    [InlineData("[]", null)]
    [InlineData("null", null)]
    [InlineData("{", null)]
    public void SelectsOnlyUnambiguousTokens(string json, string? expected) => Assert.Equal(expected, CodebuffAuthentication.Parse(json));

    [Fact]
    public void KeepsSourceAndPrecedenceWithoutReadingUnneededFile()
    {
        string? MustNotRead() => throw new InvalidOperationException();
        Assert.Equal(new("manual", false), CodebuffAuthentication.Resolve(" 'manual' ", "env", MustNotRead));
        Assert.Equal(new("env", false), CodebuffAuthentication.Resolve(" ", "\"env\"", MustNotRead));
        Assert.Equal(new("local", true), CodebuffAuthentication.Resolve(null, null, () => "local"));
        Assert.Null(CodebuffAuthentication.Resolve(null, null, () => "\r\n"));
        Assert.Null(CodebuffAuthentication.Parse(new string('x', 262145)));
    }
    [Fact]
    public void ReadsReplacementAndRejectsOversizeOrLinkedFiles()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "CodeRim-Codebuff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "credentials.json");
            Assert.Null(CodebuffAuthentication.Read(path));
            File.WriteAllText(path, """{"authToken":"first"}"""); Assert.Equal("first", CodebuffAuthentication.Read(path));
            File.WriteAllText(path, """{"authToken":"second"}"""); Assert.Equal("second", CodebuffAuthentication.Read(path));
            File.WriteAllText(path, new string(' ', 262145)); Assert.Null(CodebuffAuthentication.Read(path));
            if (!OperatingSystem.IsWindows())
            {
                var link = Path.Combine(root, "link.json"); File.CreateSymbolicLink(link, path);
                Assert.Null(CodebuffAuthentication.Read(link));
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscriptionIsUsedOnlyForLocalLogin(bool local)
    {
        var calls = 0;
        using var provider = new NativeProviders(new Handler((request, _) =>
        {
            calls++; Assert.Equal("www.codebuff.com", request.RequestUri!.Host);
            Assert.Equal("synthetic", request.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                request.RequestUri.AbsolutePath == "/api/v1/usage" ? """{"usage":12,"quota":100}""" : """{"subscription":{"displayName":"Pro"}}""") });
        }));
        var reading = await provider.FetchCodebuffAsync("synthetic", local, TestContext.Current.CancellationToken);
        Assert.Equal(local ? 2 : 1, calls); Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal(local ? "Pro" : null, reading.Plan);
    }
    [Fact]
    public async Task OptionalRateLimitDoesNotBlockPrimaryUsage()
    {
        var primary = 0;
        using var provider = new NativeProviders(new Handler((request, _) =>
        {
            var usage = request.RequestUri!.AbsolutePath == "/api/v1/usage";
            if (usage) primary++;
            return Task.FromResult(new HttpResponseMessage(usage ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests)
                { Content = new StringContent(usage ? """{"usage":12,"quota":100}""" : "{}") });
        }));
        Assert.Equal(ReadingState.Partial, (await provider.FetchCodebuffAsync("local", true, TestContext.Current.CancellationToken)).State);
        Assert.Equal(ReadingState.Ready, (await provider.FetchCodebuffAsync("api", false, TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, primary);
    }
    [Fact]
    public async Task SlowOptionalSubscriptionPreservesPrimaryUsage()
    {
        using var provider = new NativeProviders(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/v1/usage") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"usage":12,"quota":100}""") };
        }));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var reading = await provider.FetchCodebuffAsync("synthetic", true, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Partial, reading.State); Assert.Equal(12, reading.Headline!.UsedPercent);
        Assert.InRange(watch.Elapsed.TotalSeconds, 1, 10);
    }
}
