using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Tests;

public sealed class BrowserCookieTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private static BrowserCookie Cookie(string domain, string value = "fixture", string path = "/", bool hostOnly = false, long expiry = 0)
        => new("session", value, domain, path, true, hostOnly, expiry);
    [Fact]
    public void KeepsRegionsHostsPathsExpiryAndTransportSeparate()
    {
        var jar = new BrowserCookieJar([
            Cookie(".qoder.com", "global"), Cookie(".qoder.com.cn", "china"),
            Cookie("api.qoder.com", "api", "/billing", true),
            Cookie(".qoder.com", "expired", "/", false, 1799999999)
        ], ["qoder.com", "qoder.com.cn"]);
        Assert.Equal("session=global", jar.Header(new("https://qoder.com/"), Now));
        Assert.Equal("session=china", jar.Header(new("https://qoder.com.cn/"), Now));
        Assert.Equal("session=api; session=global", jar.Header(new("https://api.qoder.com/billing/usage"), Now));
        Assert.Equal("session=global", jar.Header(new("https://api.qoder.com/billing-other"), Now));
        Assert.Null(jar.Header(new("https://qoder.com.evil.invalid/"), Now));
        Assert.Null(jar.Header(new("http://qoder.com/"), Now));
        Assert.Null(jar.Header(new("https://user@qoder.com/"), Now));
        Assert.Equal(jar.Serialize(), BrowserCookieJar.Parse(jar.Serialize(), ["qoder.com", "qoder.com.cn"]).Serialize());
    }
    [Theory]
    [InlineData("injected\r\nX: value")]
    [InlineData("a; Other=x")]
    [InlineData("a,b")]
    public void RejectsHeaderInjection(string value) => Assert.Throws<InvalidDataException>(() =>
        new BrowserCookieJar([Cookie(".example.com", value)], ["example.com"]));
    [Fact]
    public void RejectsUnrelatedSitesAndOversizedBundles()
    {
        Assert.Throws<InvalidDataException>(() => new BrowserCookieJar([Cookie(".evil.invalid")], ["example.com"]));
        Assert.Throws<InvalidDataException>(() => new BrowserCookieJar(Enumerable.Repeat(Cookie(".example.com"), 513), ["example.com"]));
        Assert.Throws<InvalidDataException>(() => BrowserCookieJar.Parse(new string(' ', 262145), ["example.com"]));
    }
    [Fact]
    public void EmptyPreferenceCookiesAreValidAlongsideAuthentication()
    {
        var jar = new BrowserCookieJar([Cookie(".example.com"), new("preference", "", ".example.com", "/", true, false, 0)], ["example.com"]);
        Assert.Equal("session=fixture; preference=", jar.Header(new("https://example.com/"), Now));
        Assert.Equal(jar.Serialize(), BrowserCookieJar.Parse(jar.Serialize(), ["example.com"]).Serialize());
    }
    [Fact]
    public void RejectsBundlesThatCouldNotBeReloadedBeforeSaving()
    {
        Assert.Throws<InvalidDataException>(() => new BrowserCookieJar(
            Enumerable.Range(0, 100).Select(i => new BrowserCookie("cookie" + i, new string('a', 3000), ".example.com", "/", true, false, 0)), ["example.com"]));
        Assert.Throws<InvalidDataException>(() => new BrowserCookieJar([Cookie("..example.com")], ["example.com"]));
        Assert.Throws<InvalidDataException>(() => new BrowserCookieJar([Cookie(" .example.com")], ["example.com"]));
    }
    [Theory]
    [InlineData(15, 1)]
    [InlineData(16, 1000)]
    [InlineData(17, 1000)]
    public void ReadsOnlyLiveDefaultContainerCookiesAndPreservesTheDatabase(int version, int multiplier)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "TestResults", "coderim-browser-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "cookies.sqlite");
            using (var connection = new SqliteConnection("Data Source=" + path))
            {
                connection.Open();
                using var setup = connection.CreateCommand();
                setup.CommandText = $"PRAGMA user_version={version}; CREATE TABLE moz_cookies(name TEXT,value TEXT,host TEXT,path TEXT,isSecure INTEGER,expiry INTEGER,originAttributes TEXT)";
                setup.ExecuteNonQuery();
                void Add(string value, long expires, string attributes, string domain = ".example.com")
                {
                    using var insert = connection.CreateCommand();
                    insert.CommandText = "INSERT INTO moz_cookies VALUES('session',$v,$d,'/',1,$e,$a)";
                    insert.Parameters.AddWithValue("$v", value); insert.Parameters.AddWithValue("$d", domain);
                    insert.Parameters.AddWithValue("$e", expires * multiplier); insert.Parameters.AddWithValue("$a", attributes); insert.ExecuteNonQuery();
                }
                Add("live", Now.ToUnixTimeSeconds() + 100, "");
                Add("expired", Now.ToUnixTimeSeconds() - 1, "");
                Add("container", Now.ToUnixTimeSeconds() + 100, "^userContextId=2");
                Add("other", Now.ToUnixTimeSeconds() + 100, "", ".example.com.evil.invalid");
            }
            SqliteConnection.ClearAllPools();
            var before = File.ReadAllBytes(path);
            var jar = FirefoxCookieImport.Read(new("fixture", root), ["example.com"], Now, TestContext.Current.CancellationToken);
            Assert.Equal(1, jar.Count); Assert.Equal("session=live", jar.Header(new("https://example.com/"), Now));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
