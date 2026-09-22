using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Tests;

public sealed class MiMoFirefoxSessionTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "TestResults", "mimo-session-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private static readonly Uri Target = new("https://platform.xiaomimimo.com/api/v1/balance");
    public MiMoFirefoxSessionTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "sessionstore-backups"));
        using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "cookies.sqlite")); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version=15; CREATE TABLE moz_cookies(name TEXT,value TEXT,host TEXT,path TEXT,isSecure INTEGER,expiry INTEGER,originAttributes TEXT)";
        command.ExecuteNonQuery();
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    private static Dictionary<string, object?> Cookie(string name, string value = "session") => new()
        { ["name"] = name, ["value"] = value, ["host"] = "platform.xiaomimimo.com", ["path"] = "/", ["secure"] = true };
    private static Dictionary<string, object?>[] Pair() => [Cookie("api-platform_serviceToken"), Cookie("userId", "fixture-user")];
    private void Write(string relative, object cookies) => WriteJson(relative, JsonSerializer.Serialize(new { cookies }));
    private void WriteJson(string relative, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var encoded = new MemoryStream();
        encoded.Write("mozLz40\0"u8); Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)bytes.Length); encoded.Write(size);
        encoded.WriteByte((byte)(Math.Min(bytes.Length, 15) << 4));
        if (bytes.Length >= 15)
        {
            var remaining = bytes.Length - 15;
            while (remaining >= 255) { encoded.WriteByte(255); remaining -= 255; }
            encoded.WriteByte((byte)remaining);
        }
        encoded.Write(bytes); File.WriteAllBytes(Path.Combine(root, relative), encoded.ToArray());
    }
    private BrowserCookieJar Read() => MiMoFirefoxSessionImport.Read(new("fixture", root), Now, TestContext.Current.CancellationToken);
    private void Persist(string name, string value)
    {
        using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "cookies.sqlite")); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO moz_cookies VALUES($name,$value,'platform.xiaomimimo.com','/',1,0,'')";
        command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$value", value); command.ExecuteNonQuery();
    }
    [Fact]
    public void RecoversACompleteSameProfileSessionWithoutModifyingBrowserFiles()
    {
        Write("sessionstore-backups/recovery.jsonlz4", Pair());
        var path = Path.Combine(root, "sessionstore-backups/recovery.jsonlz4");
        var before = File.ReadAllBytes(path);
        var jar = Read();
        Assert.Equal(2, jar.Count); Assert.Equal("api-platform_serviceToken=session; userId=fixture-user", jar.Header(Target, Now));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Null(jar.Header(new("https://other.xiaomimimo.com/api/v1/balance"), Now));
    }
    [Theory]
    [InlineData("empty")]
    [InlineData("partial")]
    [InlineData("missing")]
    public void FirstValidStatePreventsOlderSessionResurrection(string state)
    {
        Write("sessionstore-backups/recovery.jsonlz4", Pair());
        if (state == "empty") Write("sessionstore.jsonlz4", Array.Empty<object>());
        else if (state == "partial") Write("sessionstore.jsonlz4", new[] { Cookie("userId") });
        else WriteJson("sessionstore.jsonlz4", "{}");
        Assert.Equal(0, Read().Count);
    }
    [Theory]
    [InlineData("""{"cookies":null}""")]
    [InlineData("""{"cookies":false}""")]
    [InlineData("""{"cookies":{}}""")]
    [InlineData("""{"cookies":[1]}""")]
    public void MalformedCookieSchemaUsesTheNextBackup(string json)
    {
        WriteJson("sessionstore.jsonlz4", json);
        Write("sessionstore-backups/recovery.jsonlz4", Pair());
        Assert.Equal(2, Read().Count);
    }
    [Fact]
    public void CompleteSessionReplacesWholeDatabaseBundleAndPartialPiecesNeverMix()
    {
        Persist("api-platform_serviceToken", "old-token");
        Write("sessionstore-backups/recovery.jsonlz4", new[] { Cookie("userId", "new-user") });
        Assert.Equal("api-platform_serviceToken=old-token", Read().Header(Target, Now));
        Persist("unrelated", "old-extra");
        Write("sessionstore-backups/recovery.jsonlz4", Pair());
        Assert.Equal("api-platform_serviceToken=session; userId=fixture-user", Read().Header(Target, Now));
    }
    [Theory]
    [InlineData("sessionstore.jsonlz4")]
    [InlineData("sessionstore-backups/recovery.jsonlz4")]
    [InlineData("sessionstore-backups/recovery.baklz4")]
    [InlineData("sessionstore-backups/previous.jsonlz4")]
    public void MalformedCandidateCanFallBackInFixedOrder(string malformed)
    {
        File.WriteAllBytes(Path.Combine(root, malformed), "not compressed JSON"u8.ToArray());
        Write("sessionstore-backups/upgrade.jsonlz4-20260921", Pair());
        Assert.Equal(2, Read().Count);
    }
    [Fact]
    public void OnlyGreatestUpgradeFilenameIsConsidered()
    {
        Write("sessionstore-backups/upgrade.jsonlz4-20260101", Pair());
        Write("sessionstore-backups/upgrade.jsonlz4-20260921", Array.Empty<object>());
        Assert.Equal(0, Read().Count);
    }
    [Theory]
    [InlineData("originAttributes", """{"userContextId":1}""")]
    [InlineData("originAttributes", """{"privateBrowsingId":true}""")]
    [InlineData("originAttributes", """{"userContextId":0.0}""")]
    [InlineData("originAttributes", """{"futureIsolation":0}""")]
    [InlineData("originAttributes", """{"partitionKey":"example.com"}""")]
    [InlineData("originAttributes", "\"^userContextId=1\"")]
    [InlineData("isPartitioned", "true")]
    [InlineData("isPartitioned", "0")]
    [InlineData("expires", "\"0\"")]
    [InlineData("expires", "-1")]
    [InlineData("expires", "0.5")]
    [InlineData("expires", "1799999999")]
    public void IsolatedAndInvalidExpiredCookiesCannotCompleteThePair(string field, string json)
    {
        var pair = Pair(); pair[0][field] = JsonSerializer.Deserialize<JsonElement>(json);
        Write("sessionstore.jsonlz4", pair);
        Assert.Equal(0, Read().Count);
    }
    [Fact]
    public void ExplicitDefaultContextAndDomainScopeArePreserved()
    {
        var pair = Pair(); pair[0]["originAttributes"] = new { userContextId = 0, privateBrowsingId = 0, partitionKey = "" };
        pair[0]["isPartitioned"] = false; pair[0]["expires"] = 0;
        Write("sessionstore.jsonlz4", pair); Assert.Equal(2, Read().Count);
        pair[0]["host"] = "xiaomimimo.com"; Write("sessionstore.jsonlz4", pair); Assert.Equal(0, Read().Count);
        pair[0]["host"] = ".xiaomimimo.com"; Write("sessionstore.jsonlz4", pair); Assert.Equal(2, Read().Count);
        pair[0]["host"] = ".xiaomimimo.com.evil.invalid"; Write("sessionstore.jsonlz4", pair); Assert.Equal(0, Read().Count);
        pair[0]["host"] = "platform.xiaomimimo.com"; pair[0]["path"] = "/api/v10";
        Write("sessionstore.jsonlz4", pair); Assert.Equal(0, Read().Count);
    }
    [Fact]
    public void ConflictingAuthCookiesDoNotSelectAnAccountByListOrder()
    {
        Write("sessionstore.jsonlz4", Pair().Append(Cookie("userId", "different-user")).ToArray());
        Assert.Equal(0, Read().Count);
        var other = Cookie("userId", "different-user"); other["host"] = ".xiaomimimo.com";
        Write("sessionstore.jsonlz4", Pair().Append(other).ToArray()); Assert.Equal(0, Read().Count);
        Write("sessionstore.jsonlz4", Pair().Append(Pair()[1]).ToArray()); Assert.Equal(2, Read().Count);
    }
    [Fact]
    public void ResourceLimitsDoNotFallBackToOlderSessions()
    {
        Write("sessionstore-backups/recovery.jsonlz4", Pair());
        var frame = new byte[12]; "mozLz40\0"u8.CopyTo(frame);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), MiMoFirefoxSessionImport.MaximumOutputBytes + 1);
        File.WriteAllBytes(Path.Combine(root, "sessionstore.jsonlz4"), frame);
        Assert.Equal(0, Read().Count);
        Write("sessionstore.jsonlz4", Enumerable.Repeat(new { name = "ignored" }, 4097).ToArray());
        Assert.Equal(0, Read().Count);
    }
    [Fact]
    public void OversizedCurrentHeaderRetainsTheDatabaseInsteadOfAnOlderSession()
    {
        Persist("api-platform_serviceToken", "database-token"); Persist("userId", "database-user");
        Write("sessionstore-backups/recovery.jsonlz4", Pair());
        Write("sessionstore.jsonlz4", new[] { Cookie("api-platform_serviceToken", new string('t', 32760)),
            Cookie("userId", new string('u', 32760)), Cookie("api-platform_ph", new string('p', 30000)) });
        Assert.Equal("api-platform_serviceToken=database-token; userId=database-user", Read().Header(Target, Now));
    }
    [Fact]
    public void DeclaredSizeMismatchIsMalformedAndCancellationPropagates()
    {
        Write("sessionstore.jsonlz4", Pair()); var path = Path.Combine(root, "sessionstore.jsonlz4");
        var frame = File.ReadAllBytes(path); frame[8]++;
        File.WriteAllBytes(path, frame); Write("sessionstore-backups/recovery.jsonlz4", Pair()); Assert.Equal(2, Read().Count);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MiMoFirefoxSessionImport.Read(new("fixture", root), Now, cancelled.Token));
    }
}
