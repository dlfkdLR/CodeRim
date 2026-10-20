using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class ClaudeDesktopAccountTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "claude-desktop-" + Guid.NewGuid().ToString("N") + ".json");
    public void Dispose() => File.Delete(path);

    [Fact]
    public void ComparesTheRecordedDesktopAccountWithTheCliProfile()
    {
        File.WriteAllText(path, "{\"oauth:tokenCacheV2\":\"opaque\",\"lastKnownAccountUuid\":\"AAA-1\"}");
        Assert.Equal("AAA-1", ClaudeDesktopAccount.AccountId(path));
        Assert.False(ClaudeDesktopAccount.Differs("aaa-1", path));
        Assert.True(ClaudeDesktopAccount.Differs("BBB-2", path));
        Assert.Equal("BBB-2", ClaudeDesktopAccount.ProfileAccountId("{\"oauthAccount\":{\"accountUuid\":\"BBB-2\"}}"));
    }

    [Fact]
    public void UnknownDesktopIsNeverReportedAsDifferent()
    {
        Assert.False(ClaudeDesktopAccount.Differs("BBB-2", path));
        File.WriteAllText(path, "not json");
        Assert.False(ClaudeDesktopAccount.Differs("BBB-2", path));
        Assert.False(ClaudeDesktopAccount.Differs(null, path));
    }
}
