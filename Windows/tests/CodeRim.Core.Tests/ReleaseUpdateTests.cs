using System.Text.Json;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class ReleaseUpdateTests
{
    private static string Release(string tag = "v2.1.6", string asset = "CodeRim-Windows-2.1.6-arm64.zip",
        string? url = null, bool prerelease = false) => JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = false, prerelease,
            assets = new[] { new { name = asset, browser_download_url = url ?? "https://github.com/dlfkdLR/CodeRim/releases/download/" + tag + "/" + asset } }
        });
    [Fact]
    public void SelectsOnlyTheMatchingArchitectureAndVersion()
    {
        var update = ReleaseUpdates.Parse(Release(), "arm64", new Version(2, 1, 5));
        Assert.True(update.IsNewer); Assert.Equal(new Version(2, 1, 6), update.Version);
        Assert.Throws<InvalidDataException>(() => ReleaseUpdates.Parse(Release(), "x64", new Version(2, 1, 5)));
        Assert.False(ReleaseUpdates.Parse(Release(), "arm64", new Version(2, 1, 6)).IsNewer);
    }
    [Fact]
    public void RejectsDraftLikeVersionsAndUntrustedDownloadOrigins()
    {
        Assert.Throws<InvalidDataException>(() => ReleaseUpdates.Parse(Release(prerelease: true), "arm64", new Version(2, 1, 5)));
        Assert.Throws<InvalidDataException>(() => ReleaseUpdates.Parse(Release(url: "https://example.invalid/unrelated.zip"), "arm64", new Version(2, 1, 5)));
        Assert.Throws<InvalidDataException>(() => ReleaseUpdates.Parse(Release(tag: "v2.1"), "arm64", new Version(2, 1, 5)));
    }
}
