using System.Net;
using System.Text;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class ReleasePackageDownloadTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coderim-update-test-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("synthetic package bytes");
    private static ReleasePackage Package(byte[]? payload = null) => ReleaseUpdates.Parse(ReleaseUpdateTests.Release(content: payload ?? Payload), "arm64", new Version(2, 1, 5)).Package!;
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private Task<DownloadedReleasePackage> Download(HttpMessageHandler handler, TimeSpan? timeout = null, TimeSpan? idle = null, ReleasePackage? package = null, CancellationToken token = default) =>
        ReleasePackageDownload.DownloadAsync(package ?? Package(), root, handler, timeout ?? TimeSpan.FromSeconds(5), idle ?? TimeSpan.FromSeconds(2), token);
    private static HttpResponseMessage Body(byte[] data, bool length = false)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChunkStream(data)) };
        if (length) response.Content.Headers.ContentLength = data.Length;
        return response;
    }
    private void AssertNoResult()
    {
        if (Directory.Exists(root)) Assert.Empty(Directory.GetFiles(root, "*.zip*"));
    }

    [Fact]
    public async Task PublishesOnlyCompleteMatchingBytesAndDoesNotOverwriteExistingFiles()
    {
        Directory.CreateDirectory(root); var existing = Path.Combine(root, "keep.txt"); await File.WriteAllTextAsync(existing, "preserve", TestContext.Current.CancellationToken);
        using var handler = new Handler(_ => Body(Payload, true));
        var first = await Download(handler, token: TestContext.Current.CancellationToken);
        var second = await Download(handler, token: TestContext.Current.CancellationToken);
        Assert.NotEqual(first.Path, second.Path); Assert.Equal(Payload, await File.ReadAllBytesAsync(first.Path, TestContext.Current.CancellationToken));
        Assert.Equal(Package(), first.Package); Assert.False(File.Exists(first.Path + ".partial"));
        Assert.Equal("preserve", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task ConcurrentDownloadsCommitSeparateResults()
    {
        using var handler = new Handler(_ => Body(Payload));
        var results = await Task.WhenAll(Download(handler, token: TestContext.Current.CancellationToken), Download(handler, token: TestContext.Current.CancellationToken));
        Assert.NotEqual(results[0].Path, results[1].Path); Assert.Equal(2, Directory.GetFiles(root).Length);
    }
    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("digest")]
    [InlineData("header")]
    [InlineData("encoding")]
    [InlineData("partial")]
    [InlineData("status")]
    public async Task RejectsUnusableResponsesWithoutLeavingAResult(string scenario)
    {
        using var handler = new Handler(_ =>
        {
            var data = scenario == "short" ? Payload[..^1] : scenario == "long" ? Payload.Concat(new byte[] { 1 }).ToArray() : scenario == "digest" ? Payload.Select(value => (byte)(value ^ 1)).ToArray() : Payload;
            var response = Body(data);
            if (scenario == "header") response.Content.Headers.ContentLength = Payload.Length + 1;
            if (scenario == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            if (scenario == "partial") response.StatusCode = HttpStatusCode.PartialContent;
            if (scenario == "status") response.StatusCode = HttpStatusCode.Unauthorized;
            return response;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(handler, token: TestContext.Current.CancellationToken)); AssertNoResult();
    }
    [Fact]
    public async Task OversizedChunkedBodyStopsAfterExpectedLengthPlusOne()
    {
        using var stream = new CountingStream(new byte[4096]);
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(handler, token: TestContext.Current.CancellationToken));
        Assert.Equal(Payload.Length + 1, stream.BytesRead); AssertNoResult();
    }
    [Fact]
    public async Task FollowsOnlyBoundedHttpsReleaseAssetRedirectsWithoutAmbientHeaders()
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            calls++;
            if (calls == 1) return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/synthetic?signature=fixture") } };
            Assert.Equal("release-assets.githubusercontent.com", request.RequestUri!.Host); return Body(Payload);
        });
        var result = await Download(handler, token: TestContext.Current.CancellationToken); Assert.Equal(2, calls); Assert.True(File.Exists(result.Path));
    }
    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://release-assets.githubusercontent.com:444/file")]
    [InlineData("https://release-assets.githubusercontent.com.evil.invalid/file")]
    [InlineData("https://user@release-assets.githubusercontent.com/file")]
    [InlineData("https://release-assets.githubusercontent.com/file#fragment")]
    [InlineData("https://github.com/another/repository/file")]
    [InlineData("https://127.0.0.1/file")]
    [InlineData("file:///tmp/package.zip")]
    public async Task RejectsRedirectBeforeSendingToForbiddenDestination(string location)
    {
        var calls = 0;
        using var handler = new Handler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri(location) } }; });
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(handler, token: TestContext.Current.CancellationToken));
        Assert.Equal(1, calls); AssertNoResult();
    }
    [Fact]
    public async Task LimitsRedirectCountAndDetectsCycles()
    {
        var calls = 0;
        using var chain = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://objects.githubusercontent.com/file/" + ++calls) } });
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(chain, token: TestContext.Current.CancellationToken)); Assert.Equal(4, calls);
        calls = 0;
        using var cycle = new Handler(request => { calls++; return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = request.RequestUri } }; });
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(cycle, token: TestContext.Current.CancellationToken)); Assert.Equal(1, calls); AssertNoResult();
    }
    [Fact]
    public async Task CancellationRemovesPartialAndNeverPublishesLateRead()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stream = new StalledStream();
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        var download = Download(handler, token: cancellation.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Single(Directory.GetFiles(root, "*.partial")); Assert.Empty(Directory.GetFiles(root, "*.zip"));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        AssertNoResult(); stream.Complete(0); await Task.Yield(); AssertNoResult();
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BoundsUncooperativeBodyByIdleAndTotalDeadline(bool idleExpiresFirst)
    {
        var stream = new StalledStream();
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        var download = Download(handler, token: TestContext.Current.CancellationToken,
            timeout: idleExpiresFirst ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100),
            idle: idleExpiresFirst ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        AssertNoResult(); stream.Complete(0);
    }
    [Fact]
    public async Task BoundsUncooperativeHeadersAndDisposesLateResponse()
    {
        using var handler = new StalledHandler();
        var download = Download(handler, token: TestContext.Current.CancellationToken, timeout: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        var content = new DisposalContent(); handler.Complete(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        await content.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken); AssertNoResult();
    }
    [Fact]
    public async Task AlreadyCanceledCallDoesNotSendOrCreateFiles()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var handler = new Handler(_ => throw new InvalidOperationException("No request expected."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download(handler, token: cancellation.Token)); Assert.False(Directory.Exists(root));
    }
    [Fact]
    public void DefaultTransportUsesAnonymousSystemProxyWithoutCookiesOrImplicitRedirects()
    {
        using var handler = ReleasePackageDownload.CreateHandler();
        Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseCookies); Assert.False(handler.UseDefaultCredentials);
        Assert.Null(handler.Credentials); Assert.Null(handler.DefaultProxyCredentials); Assert.True(handler.UseProxy);
        Assert.NotNull(handler.Proxy); Assert.Null(handler.Proxy.Credentials);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public void ProxyRoutingIsPreservedWithoutReadingOrForwardingItsCredentials()
    {
        using var handler = ReleasePackageDownload.CreateHandler(new CredentialTrapProxy());
        var destination = new Uri("https://github.com/"); var proxy = handler.Proxy!;
        Assert.False(proxy.IsBypassed(destination));
        Assert.Equal("http://proxy.invalid:8080/", proxy.GetProxy(destination)!.AbsoluteUri);
        Assert.True(proxy.IsBypassed(new Uri("https://bypass.invalid/"))); Assert.Null(proxy.Credentials);
    }
    private sealed class CredentialTrapProxy : IWebProxy
    {
        public ICredentials? Credentials { get => throw new InvalidOperationException("Credentials must not be read."); set => throw new NotSupportedException(); }
        public Uri? GetProxy(Uri destination) => new Uri("http://fixture-user:fixture-secret@proxy.invalid:8080/");
        public bool IsBypassed(Uri host) => host.Host == "bypass.invalid";
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie")); Assert.False(request.Headers.Contains("Proxy-Authorization"));
            return Task.FromResult(response(request));
        }
    }
    private sealed class ChunkStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }
    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        internal long BytesRead { get; private set; }
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken); BytesRead += count; return count;
        }
    }
    private sealed class StalledStream : Stream
    {
        private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete(int count) => completion.TrySetResult(count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { Started.TrySetResult(); return new(completion.Task); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class StalledHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete(HttpResponseMessage response) => completion.TrySetResult(response);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => completion.Task;
    }
    private sealed class DisposalContent : ByteArrayContent
    {
        internal DisposalContent() : base(Payload) { }
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) Disposed.TrySetResult(); }
    }
}
