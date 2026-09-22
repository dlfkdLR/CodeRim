using System.Text;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
        var before = SnapshotSharedFile(Database); var walBefore = SnapshotSharedFile(Database + "-wal");
        Assert.Equal("from-wal", KimiDesktopAuthentication.Read(Database, Now, TestContext.Current.CancellationToken).Token);
        Assert.Equal(before, SnapshotSharedFile(Database)); Assert.Equal(walBefore, SnapshotSharedFile(Database + "-wal"));
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
        var linked = Path.Combine(root, "linked");
        if (OperatingSystem.IsWindows())
        {
            // A junction exercises the ancestor reparse boundary without enabling Developer Mode
            // or requiring the privilege needed for file symbolic links.
            CreateJunction(linked, root);
            try
            {
                Assert.True((File.GetAttributes(linked) & FileAttributes.ReparsePoint) != 0);
                Assert.True(File.Exists(Path.Combine(linked, "Cookies")));
                Assert.Throws<InvalidDataException>(() => KimiDesktopAuthentication.Read(Path.Combine(linked, "Cookies"), Now, TestContext.Current.CancellationToken));
            }
            finally { Directory.Delete(linked); }
        }
        else
        {
            File.CreateSymbolicLink(linked, Database);
            Assert.Throws<InvalidDataException>(() => KimiDesktopAuthentication.Read(linked, Now, TestContext.Current.CancellationToken));
        }
    }
    private static byte[] SnapshotSharedFile(string path)
    {
        // The fixture's SQLite writer remains open. Observe committed bytes without
        // denying that existing writer's access on Windows.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.InRange(file.Length, 0, 1024 * 1024);
        using var bytes = new MemoryStream();
        file.CopyTo(bytes);
        return bytes.ToArray();
    }
    private static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        using var handle = CreateFileW(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var display = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var data = new byte[16 + substitute.Length + 2 + display.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(data.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), checked((ushort)display.Length));
        substitute.CopyTo(data, 16); display.CopyTo(data, 18 + substitute.Length);
        if (!DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int length, IntPtr output, int outputLength, out int returned, IntPtr overlapped);
    [Theory]
    [InlineData("Cookies")]
    [InlineData(@"\\server\share\Cookies")]
    [InlineData("//server/share/Cookies")]
    public void OnlyExplicitLocalPathsAreRead(string path)
    {
        Assert.Throws<InvalidDataException>(() => KimiDesktopAuthentication.Read(path, Now, TestContext.Current.CancellationToken));
    }
}
