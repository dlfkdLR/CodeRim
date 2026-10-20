using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;
namespace CodeRim.Core.Tests;

public sealed class BrowserSqliteSafetyTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "browser-safety-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] Domains = ["example.com"];
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private const string Table = "CREATE TABLE moz_cookies(name TEXT,value TEXT,host TEXT,path TEXT,isSecure INTEGER,expiry INTEGER,originAttributes TEXT)";
    public BrowserSqliteSafetyTests() => Directory.CreateDirectory(directory);
    private SqliteConnection Create(string schema)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "cookies.sqlite"), Pooling = false }.ToString());
        connection.Open(); using var setup = connection.CreateCommand(); setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA user_version=17; " + schema; setup.ExecuteNonQuery(); return connection;
    }
    private BrowserCookieJar Read() => FirefoxCookieImport.Read(new("fixture", directory), Domains, Now, TestContext.Current.CancellationToken);
    [Theory]
    [InlineData("CREATE VIEW moz_cookies AS SELECT 'session' AS name,(WITH RECURSIVE c(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM c WHERE x<100000000) SELECT sum(x) FROM c) AS value,'.example.com' AS host,'/' AS path,1 AS isSecure,0 AS expiry,'' AS originAttributes")]
    [InlineData("CREATE TABLE moz_cookies(name TEXT,value TEXT GENERATED ALWAYS AS (hex(zeroblob(100000000))) VIRTUAL,host TEXT,path TEXT,isSecure INTEGER,expiry INTEGER,originAttributes TEXT)")]
    [InlineData("CREATE VIRTUAL TABLE moz_cookies USING fts5(name,value,host,path,isSecure,expiry,originAttributes)")]
    public void RejectsExecutableCookieSchemas(string schema)
    {
        using var connection = Create(schema);
        Assert.Throws<InvalidDataException>(() => Read());
    }
    [Theory]
    [InlineData(1, 32769)]
    [InlineData(10, 30000)]
    public void BoundsIndividualAndCombinedCookieValuesBeforeCollectingThem(int count, int length)
    {
        using var connection = Create(Table); using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO moz_cookies VALUES('session',$value,'.example.com','/',1,0,'')";
        insert.Parameters.AddWithValue("$value", new string('x', length));
        for (var i = 0; i < count; i++) insert.ExecuteNonQuery();
        Assert.Throws<InvalidDataException>(() => Read());
    }
    [Fact]
    public void UnindexedCookieScanHasAVmBudget()
    {
        using var connection = Create(Table); using var insert = connection.CreateCommand();
        insert.CommandText = "WITH RECURSIVE c(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM c WHERE x<400000) INSERT INTO moz_cookies SELECT 'session','value','.other.invalid','/',1,0,'' FROM c";
        insert.ExecuteNonQuery();
        Assert.Equal(9, Assert.Throws<SqliteException>(() => Read()).SqliteErrorCode);
    }
    [Fact]
    public void CancelledReadDoesNotOpenAProfile()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => FirefoxCookieImport.Read(new("missing", Path.Combine(directory, "missing")), Domains, Now, cancel.Token));
    }
    [Fact]
    public void ReadsLiveCommittedWalWithoutModifyingDatabaseBytes()
    {
        using var connection = Create(Table); using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO moz_cookies VALUES('session','from-wal','.example.com','/',1,0,'')"; insert.ExecuteNonQuery();
        byte[] Snapshot(string suffix)
        {
            using var stream = new FileStream(Path.Combine(directory, "cookies.sqlite" + suffix), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var result = new MemoryStream(); stream.CopyTo(result); return result.ToArray();
        }
        var database = Snapshot(""); var wal = Snapshot("-wal");
        Assert.Equal("session=from-wal", Read().Header(new("https://example.com/"), Now));
        Assert.Equal(database, Snapshot("")); Assert.Equal(wal, Snapshot("-wal"));
    }
    public void Dispose() => Directory.Delete(directory, true);
}
