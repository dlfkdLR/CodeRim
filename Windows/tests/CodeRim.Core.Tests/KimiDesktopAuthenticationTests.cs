using System.Text;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Tests;

public sealed class KimiDesktopAuthenticationTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "TestResults", "coderim-kimi-desktop-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(root, "Cookies");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
    public KimiDesktopAuthenticationTests() { Directory.CreateDirectory(root); }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    private SqliteConnection Create(string? schema = null)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = schema ?? "CREATE TABLE cookies(host_key TEXT,name TEXT,value TEXT,encrypted_value BLOB,last_access_utc INTEGER)";
        command.ExecuteNonQuery(); return connection;
    }
    private static void Insert(SqliteConnection db, string value, long accessed = 1, string host = ".kimi.com", string name = "kimi-auth")
    {
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO cookies(host_key,name,value,last_access_utc) VALUES($host,$name,$value,$accessed)";
        command.Parameters.AddWithValue("$host", host); command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value); command.Parameters.AddWithValue("$accessed", accessed);
        command.ExecuteNonQuery();
    }
    private static string Jwt(string claims) => "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
    [Fact]
    public void NewestSelectedHostIsReadWithoutChangingDatabase()
    {
        using (var db = Create()) { Insert(db, "older"); Insert(db, " selected-token ", 2); Insert(db, "unrelated", 3, "other.example"); }
        var before = File.ReadAllBytes(Database);
        var result = KimiDesktopAuthentication.Read(Database, Now, TestContext.Current.CancellationToken);
        Assert.Equal("web", result.Source); Assert.Equal("selected-token", result.Token); Assert.Equal(Database, result.FilePath);
        Assert.Null(result.ApiBaseUrl); Assert.Null(result.BrowserState); Assert.Equal(before, File.ReadAllBytes(Database));
    }
    [Theory]
    [InlineData("www.kimi.com")]
    [InlineData(".www.kimi.com")]
    [InlineData(".kimi.com")]
    [InlineData("kimi.com")]
    public void AllPinnedHostsAreSupported(string host)
    {
        using var db = Create(); Insert(db, "session", host: host);
        Assert.Equal("session", KimiDesktopAuthentication.Read(Database, Now, TestContext.Current.CancellationToken).Token);
    }
    [Theory]
    [InlineData("evil.kimi.com", "kimi-auth")]
    [InlineData("kimi.com.attacker.test", "kimi-auth")]
    [InlineData("KIMI.COM", "kimi-auth")]
    [InlineData("kimi.com", "other")]
    [InlineData("kimi.com", "KIMI-AUTH")]
    public void UnselectedHostOrCookieDoesNotBecomeAConnection(string host, string name)
    {
        using var db = Create(); Insert(db, "session", host: host, name: name);
        Assert.Null(KimiDesktopAuthentication.Read(Database, Now, TestContext.Current.CancellationToken).Token);
    }
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("header.eyJleHAiOjE5OTk5OTk5OTl9.signature")]
    public void EmptyEncryptedOrExpiredNewestNeverRevivesOlderToken(string value)
    {
        using var db = Create(); Insert(db, "older"); Insert(db, value, 2);
        using var encrypted = db.CreateCommand(); encrypted.CommandText = "UPDATE cookies SET encrypted_value=x'7632306e6f742d7265616c' WHERE last_access_utc=2"; encrypted.ExecuteNonQuery();
        Assert.Null(KimiDesktopAuthentication.Read(Database, Now, TestContext.Current.CancellationToken).Token);
    }
    [Fact]
    public void FutureJwtExpiryIsOnlyAHintAndNotAnAccountIdentity()
    {
        using var db = Create(); var token = Jwt("""{"exp":2000000060,"sub":"not-authenticated"}"""); Insert(db, token);
        var result = KimiDesktopAuthentication.Read(Database, Now, TestContext.Current.CancellationToken);
        Assert.Equal(token, result.Token); Assert.Equal(2_000_000_060d, result.ExpiresAt); Assert.Null(result.DeviceId);
    }
    [Theory]
    [InlineData("""{"exp":2000000000}""")]
    [InlineData("""{"exp":1e100}""")]
    [InlineData("""{"exp":"2000000060"}""")]
    [InlineData("""{"exp":2000000060,"exp":1}""")]
    public void ExpiredOrAmbiguousExpiryCannotBeUsed(string claims)
    {
        using var db = Create(); Insert(db, Jwt(claims));
        Assert.Null(KimiDesktopAuthentication.Read(Database, Now, TestContext.Current.CancellationToken).Token);
    }
    [Fact]
    public void EqualNewestTimestampsAreAmbiguous()
    {
        using var db = Create(); Insert(db, "one"); Insert(db, "two");
        Assert.Throws<InvalidDataException>(() => KimiDesktopAuthentication.Read(Database, Now, TestContext.Current.CancellationToken));
    }
    [Fact]
    public void OversizedValueIsRejectedBeforeMaterialization()
    {
        using var db = Create(); Insert(db, new string('a', 32769));
        Assert.Throws<InvalidDataException>(() => KimiDesktopAuthentication.Read(Database, Now, TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData("CREATE VIEW cookies AS SELECT '.kimi.com' host_key,'kimi-auth' name,'token' value,1 last_access_utc")]
    [InlineData("CREATE TABLE cookies(host_key TEXT,name TEXT,value TEXT GENERATED ALWAYS AS ('token'),last_access_utc INTEGER)")]
    [InlineData("CREATE TABLE cookies(host_key TEXT,name TEXT,value TEXT)")]
    public void ExecutableOrIncompleteSchemaIsRejected(string schema)
    {
        using var db = Create(schema);
        Assert.Throws<InvalidDataException>(() => KimiDesktopAuthentication.Read(Database, Now, TestContext.Current.CancellationToken));
    }
    [Fact]
    public void ActiveWalReadsCommittedTokenWithoutRewritingCredentialData()
    {
        using var db = Create(); using var mode = db.CreateCommand(); mode.CommandText = "PRAGMA journal_mode=WAL"; mode.ExecuteScalar();
        Insert(db, "from-wal");
        var before = File.ReadAllBytes(Database); var walBefore = File.ReadAllBytes(Database + "-wal");
        Assert.Equal("from-wal", KimiDesktopAuthentication.Read(Database, Now, TestContext.Current.CancellationToken).Token);
        Assert.Equal(before, File.ReadAllBytes(Database)); Assert.Equal(walBefore, File.ReadAllBytes(Database + "-wal"));
        // SQLite may coordinate readers through SHM; it is not credential content.
    }
    [Fact]
    public void IdleWalDatabaseCanCreateEmptyReaderCoordinationWithoutChangingCredentials()
    {
        using (var db = Create())
        {
            using var mode = db.CreateCommand(); mode.CommandText = "PRAGMA journal_mode=WAL"; mode.ExecuteScalar();
            Insert(db, "from-idle-wal");
        }
        Assert.False(File.Exists(Database + "-wal"));
        var before = File.ReadAllBytes(Database);
        Assert.Equal("from-idle-wal", KimiDesktopAuthentication.Read(Database, Now, TestContext.Current.CancellationToken).Token);
        Assert.Equal(before, File.ReadAllBytes(Database));
    }
    [Fact]
    public void CancelledReadDoesNotOpenDatabase()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => KimiDesktopAuthentication.Read(Database, Now, cancelled.Token));
        Assert.False(File.Exists(Database));
    }
    [Fact]
    public void LinkedDatabaseIsRejected()
    {
        using (var db = Create()) Insert(db, "token");
        var linked = Path.Combine(root, "linked"); File.CreateSymbolicLink(linked, Database);
        Assert.Throws<InvalidDataException>(() => KimiDesktopAuthentication.Read(linked, Now, TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData("Cookies")]
    [InlineData(@"\\server\share\Cookies")]
    [InlineData("//server/share/Cookies")]
    public void OnlyExplicitLocalPathsAreRead(string path)
    {
        Assert.Throws<InvalidDataException>(() => KimiDesktopAuthentication.Read(path, Now, TestContext.Current.CancellationToken));
    }
}
