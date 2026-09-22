using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class ReleaseUpdateTests
{
    internal static string Release(string tag = "v2.1.6", string asset = "CodeRim-Windows-2.1.6-arm64.zip",
        string? url = null, bool prerelease = false, byte[]? content = null) => JsonSerializer.Serialize(new
    {
        id = 17, tag_name = tag, draft = false, prerelease,
        assets = new[] { new { id = 19, state = "uploaded", name = asset,
            size = (content ?? [1, 2, 3, 4]).Length,
            digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content ?? [1, 2, 3, 4])),
            browser_download_url = url ?? "https://github.com/dlfkdLR/CodeRim/releases/download/" + tag + "/" + asset } }
    });
    [Fact]
    public void SelectsOnlyTheMatchingArchitectureAndVersion()
    {
        var update = ReleaseUpdates.Parse(Release(), "arm64", new Version(2, 1, 5));
        Assert.True(update.IsNewer); Assert.Equal(new Version(2, 1, 6), update.Version);
        Assert.Throws<InvalidDataException>(() => ReleaseUpdates.Parse(Release(), "x64", new Version(2, 1, 5)));
        Assert.False(ReleaseUpdates.Parse(Release(), "arm64", new Version(2, 1, 6)).IsNewer);
        Assert.False(ReleaseUpdates.Parse(Release(), "arm64", new Version(2, 2, 0)).IsNewer);
        var package = Assert.IsType<ReleasePackage>(update.Package);
        Assert.Equal(17, package.ReleaseId); Assert.Equal(19, package.AssetId); Assert.Equal(4, package.Size);
        Assert.Equal(update.Download, package.Download); Assert.Equal(update.Version, package.Version);
        Assert.Equal("arm64", package.Architecture); Assert.Equal("CodeRim-Windows-2.1.6-arm64.zip", package.FileName);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3, 4 })), package.Sha256);
    }
    [Fact]
    public void RejectsDraftLikeVersionsAndUntrustedDownloadOrigins()
    {
        Assert.Throws<InvalidDataException>(() => ReleaseUpdates.Parse(Release(prerelease: true), "arm64", new Version(2, 1, 5)));
        Assert.Throws<InvalidDataException>(() => ReleaseUpdates.Parse(Release(url: "https://example.invalid/unrelated.zip"), "arm64", new Version(2, 1, 5)));
        Assert.Throws<InvalidDataException>(() => ReleaseUpdates.Parse(Release(tag: "v2.1"), "arm64", new Version(2, 1, 5)));
    }
    [Theory]
    [InlineData("v2.1.6.0")]
    [InlineData("v02.1.6")]
    [InlineData("v2.01.6")]
    [InlineData("v2.1.06")]
    [InlineData("v2.1.6-beta")]
    [InlineData("v2.1.6 ")]
    [InlineData("V2.1.6")]
    [InlineData("v+2.1.6")]
    public void RejectsNonCanonicalStableTags(string tag) =>
        Assert.Throws<InvalidDataException>(() => ReleaseUpdates.Parse(Release(tag: tag), "arm64", new Version(2, 1, 5)));

    [Theory]
    [InlineData("size", "0")]
    [InlineData("size", "-1")]
    [InlineData("size", "536870913")]
    [InlineData("size", "1.5")]
    [InlineData("size", "\"4\"")]
    [InlineData("id", "0")]
    [InlineData("id", "9223372036854775808")]
    [InlineData("digest", "\"sha256:bad\"")]
    [InlineData("digest", "42")]
    [InlineData("state", "\"new\"")]
    [InlineData("state", "42")]
    public void RejectsInvalidAssetMetadata(string field, string value)
    {
        var root = JsonNode.Parse(Release())!; root["assets"]![0]![field] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => ReleaseUpdates.Parse(root.ToJsonString(), "arm64", new Version(2, 1, 5)));
    }
    [Theory]
    [InlineData("digest")]
    [InlineData("size")]
    [InlineData("id")]
    public void OlderIncompleteAssetsStillProvideManualLinks(string field)
    {
        var root = JsonNode.Parse(Release())!; root["assets"]![0]!.AsObject().Remove(field);
        var update = ReleaseUpdates.Parse(root.ToJsonString(), "arm64", new Version(2, 1, 5));
        Assert.True(update.IsNewer); Assert.Null(update.Package); Assert.Contains("/releases/download/v2.1.6/", update.Download.AbsoluteUri, StringComparison.Ordinal);
    }
    [Fact]
    public void MissingReleaseIdOrNullDigestNeverProducesADownloadContract()
    {
        var root = JsonNode.Parse(Release())!; root.AsObject().Remove("id");
        Assert.Null(ReleaseUpdates.Parse(root.ToJsonString(), "arm64", new Version(2, 1, 5)).Package);
        root["id"] = 17; root["assets"]![0]!["digest"] = null;
        Assert.Null(ReleaseUpdates.Parse(root.ToJsonString(), "arm64", new Version(2, 1, 5)).Package);
    }
    [Fact]
    public void RejectsDuplicateIdentityOrMatchingAssets()
    {
        var root = JsonNode.Parse(Release())!; root["assets"]!.AsArray().Add(root["assets"]![0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => ReleaseUpdates.Parse(root.ToJsonString(), "arm64", new Version(2, 1, 5)));
        Assert.Throws<InvalidDataException>(() => ReleaseUpdates.Parse(Release().Replace("\"id\":17", "\"id\":18,\"id\":17", StringComparison.Ordinal), "arm64", new Version(2, 1, 5)));
    }
    [Theory]
    [InlineData("?download=1")]
    [InlineData("#other")]
    public void RequiresTheExactDownloadUrl(string suffix)
    {
        var root = JsonNode.Parse(Release())!; root["assets"]![0]!["browser_download_url"] = root["assets"]![0]!["browser_download_url"]!.GetValue<string>() + suffix;
        Assert.Throws<InvalidDataException>(() => ReleaseUpdates.Parse(root.ToJsonString(), "arm64", new Version(2, 1, 5)));
    }
    [Fact]
    public async Task ReadsMetadataWithoutUserCredentialsAndRejectsMalformedUtf8()
    {
        using var handler = new MetadataHandler(new StringContent("[" + Release() + "]"));
        var result = await ReleaseUpdates.CheckAsync("arm64", handler, TestContext.Current.CancellationToken);
        Assert.NotNull(result.Package); Assert.Equal(1, handler.Calls);
        using var invalid = new MetadataHandler(new ByteArrayContent([0xff, 0xfe]));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseUpdates.CheckAsync("arm64", invalid, TestContext.Current.CancellationToken));
        Assert.IsType<System.Text.DecoderFallbackException>(error.InnerException);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsOversizedMetadataWithoutBufferingTheRemainingBody(bool declaresLength)
    {
        using var stream = new CountingStream(new byte[ReleaseUpdates.MaximumMetadataBytes + 65536]);
        using var content = new StreamContent(stream);
        if (declaresLength) content.Headers.ContentLength = ReleaseUpdates.MaximumMetadataBytes + 1;
        using var handler = new MetadataHandler(content);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseUpdates.CheckAsync("arm64", handler, TestContext.Current.CancellationToken));
        Assert.Equal(declaresLength ? 0 : ReleaseUpdates.MaximumMetadataBytes + 8192, stream.BytesRead);
    }
    [Fact]
    public async Task FindsHighestWindowsVersionDespiteMacOnlyLatestAndOutOfOrderReleases()
    {
        using var handler = new PagesHandler(_ => Page(Release(tag: "v9.0.0", asset: "CodeRim-9.0.0.zip"),
            Release(tag: "v2.1.8", asset: "CodeRim-Windows-2.1.8-arm64.zip"), Release(),
            Release(tag: "v99.0.0", asset: "CodeRim-Windows-99.0.0-arm64.zip", prerelease: true)));
        var update = await ReleaseUpdates.CheckAsync("arm64", handler, TestContext.Current.CancellationToken);
        Assert.Equal(new Version(2, 1, 8), update.Version); Assert.NotNull(update.Package); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task FindsWindowsReleaseOnSecondPage()
    {
        using var handler = new PagesHandler(page => page == 1 ? FullMacPage() : Page(Release()));
        var update = await ReleaseUpdates.CheckAsync("arm64", handler, TestContext.Current.CancellationToken);
        Assert.Equal(new Version(2, 1, 6), update.Version); Assert.Equal(2, handler.Calls);
    }
    [Theory]
    [InlineData("bad-url")]
    [InlineData("bad-digest")]
    [InlineData("bad-tag")]
    [InlineData("duplicate")]
    public async Task DoesNotSkipBrokenOrAmbiguousWindowsReleases(string scenario)
    {
        var invalid = JsonNode.Parse(Release(tag: "v2.1.8", asset: "CodeRim-Windows-2.1.8-arm64.zip"))!;
        if (scenario == "bad-url") invalid["assets"]![0]!["browser_download_url"] = "https://example.invalid/package.zip";
        if (scenario == "bad-digest") invalid["assets"]![0]!["digest"] = "sha256:invalid";
        if (scenario == "bad-tag") invalid["tag_name"] = "v2.1.8.0";
        using var handler = new PagesHandler(_ => Page(Release(), scenario == "duplicate" ? Release() : invalid.ToJsonString()));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseUpdates.CheckAsync("arm64", handler, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task SearchLimitNeverClaimsAnUnprovenLatestVersion()
    {
        // Use distinct versions on successive pages so this exercises the page ceiling rather than duplicate detection.
        using var pages = new PagesHandler(page => Page(Enumerable.Repeat(Release(asset: "CodeRim-macOS.zip"), ReleaseUpdates.ReleasePageSize - 1)
            .Append(Release(tag: "v2.1." + page, asset: "CodeRim-Windows-2.1." + page + "-arm64.zip")).ToArray()));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseUpdates.CheckAsync("arm64", pages, TestContext.Current.CancellationToken));
        Assert.Contains("search limit", error.Message, StringComparison.Ordinal); Assert.Equal(ReleaseUpdates.MaximumReleasePages, pages.Calls);
    }
    [Theory]
    [InlineData("object")]
    [InlineData("too-many-items")]
    [InlineData("no-windows")]
    public async Task RejectsInvalidOrEmptyWindowsLists(string scenario)
    {
        using var handler = new PagesHandler(_ => scenario == "object" ? Release() : scenario == "too-many-items" ? Page(Enumerable.Repeat(Release(asset: "CodeRim-macOS.zip"), ReleaseUpdates.ReleasePageSize + 1).ToArray()) : "[]");
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseUpdates.CheckAsync("arm64", handler, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task EnforcesOneTotalMetadataBudgetAcrossPages()
    {
        var large = JsonNode.Parse(Release(asset: "CodeRim-macOS.zip"))!; large["body"] = new string('x', 600000);
        var page = Page(Enumerable.Repeat(Release(asset: "CodeRim-macOS.zip"), ReleaseUpdates.ReleasePageSize - 1).Append(large.ToJsonString()).ToArray());
        using var handler = new PagesHandler(_ => page);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseUpdates.CheckAsync("arm64", handler, TestContext.Current.CancellationToken));
        Assert.Contains("too large", error.Message, StringComparison.Ordinal); Assert.Equal(2, handler.Calls);
    }
    [Theory]
    [InlineData(206, false, false)]
    [InlineData(200, true, false)]
    [InlineData(200, false, true)]
    [InlineData(201, false, false)]
    public async Task RejectsIncompleteOrUnexpectedMetadataResponses(int status, bool hasRange, bool encoded)
    {
        using var content = new StringContent(Page(Release()));
        if (hasRange) content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 50, 100);
        if (encoded) content.Headers.ContentEncoding.Add("gzip");
        using var handler = new MetadataHandler(content, (HttpStatusCode)status);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseUpdates.CheckAsync("arm64", handler, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task CancellationAtMetadataEofCannotPublishAnUpdate()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var stream = new CancelAtEndStream(System.Text.Encoding.UTF8.GetBytes(Page(Release())), cancellation);
        using var handler = new MetadataHandler(new StreamContent(stream));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReleaseUpdates.CheckAsync("arm64", handler, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
    }
    private sealed class CancelAtEndStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            if (count == 0) cancellation.Cancel();
            return count;
        }
    }
    private static string Page(params string[] releases) => "[" + string.Join(',', releases) + "]";
    private static string FullMacPage() => Page(Enumerable.Repeat(Release(asset: "CodeRim-macOS.zip"), ReleaseUpdates.ReleasePageSize).ToArray());
    private sealed class PagesHandler(Func<int, string> page) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("https://api.github.com/repos/dlfkdLR/CodeRim/releases?per_page=30&page=" + Calls, request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie")); Assert.False(request.Headers.Contains("Proxy-Authorization"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page(Calls)) });
        }
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
    private sealed class MetadataHandler(HttpContent content, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Assert.Equal("https://api.github.com/repos/dlfkdLR/CodeRim/releases?per_page=30&page=1", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }
}
