using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace CodeRim.Core.Tests;

public sealed class CursorAuthenticationTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "TestResults", "cursor-agent-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> environment = new(StringComparer.Ordinal);
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private string Roaming => Path.Combine(root, "AppData", "Roaming");
    private string Auth => Path.Combine(Roaming, "Cursor", "auth.json");
    private string Config => Path.Combine(root, ".cursor", "cli-config.json");
    public CursorAuthenticationTests() => Directory.CreateDirectory(root);
    private CursorLocalConnection Read() => CursorAuthentication.Read(root, Roaming, environment.GetValueOrDefault, Now);
    private static string Jwt(string subject = "agent-owner", long expiration = 1800003600)
        => "header." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { sub = subject, exp = expiration })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture-signature";
    private static void Write(string path, string text)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
    private void Agent(string? token = null, string? email = "agent@example.invalid", string? authId = "agent-owner", object? userId = null)
    {
        Write(Auth, JsonSerializer.Serialize(new { accessToken = token ?? Jwt(), refreshToken = "never-borrow", apiKey = "never-borrow-key" }));
        Write(Config, JsonSerializer.Serialize(new { authInfo = new { email, authId, userId } }));
    }
    private void Editor(string? token = "editor-token", string? email = "editor@example.invalid")
    {
        var path = Path.Combine(Roaming, "Cursor", "User", "globalStorage", "state.vscdb");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = "CREATE TABLE ItemTable(key TEXT PRIMARY KEY, value TEXT); INSERT INTO ItemTable VALUES('cursorAuth/accessToken',$token),('cursorAuth/stripeMembershipAuthId','editor-owner'),('cursorAuth/cachedEmail',$email),('cursorAuth/stripeMembershipType','pro')";
        setup.Parameters.AddWithValue("$token", (object?)token ?? DBNull.Value); setup.Parameters.AddWithValue("$email", (object?)email ?? DBNull.Value); setup.ExecuteNonQuery();
    }

    [Fact]
    public void CurrentWindowsAgentFilesProvideOneReadOnlyConnection()
    {
        Agent(); var auth = File.ReadAllBytes(Auth); var config = File.ReadAllBytes(Config);
        var result = Read();
        Assert.Equal("WorkosCursorSessionToken=agent-owner::" + Jwt(), result.Login?.Credential);
        Assert.Equal("agent@example.invalid", result.Summary?.Account?.Label);
        Assert.Equal("cursor-agent", result.Login?.Account.Source); Assert.Null(result.Summary?.Plan);
        Assert.Equal(auth, File.ReadAllBytes(Auth)); Assert.Equal(config, File.ReadAllBytes(Config));
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(Jwt(), json, StringComparison.Ordinal); Assert.DoesNotContain(result.Summary!.Version, json, StringComparison.Ordinal);
        Assert.Equal("Detected Cursor connection", result.ToString());
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void EditorAlwaysOwnsItsQuotaEvenWithoutCachedEmail(bool email)
    {
        Editor(email: email ? "editor@example.invalid" : null); Agent(); var result = Read();
        Assert.Equal("WorkosCursorSessionToken=editor-owner::editor-token", result.Login?.Credential);
        Assert.Equal(email ? "editor@example.invalid" : null, result.Summary?.Account?.Label);
        Assert.Equal(email ? "pro" : null, result.Summary?.Plan);
        Assert.DoesNotContain("agent@example.invalid", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void AgentQuotaNeverAcquiresTokenlessEditorIdentity()
    {
        Editor(token: null); Agent(); var result = Read();
        Assert.Equal("agent@example.invalid", result.Login?.Account.Label);
        Assert.Equal("agent@example.invalid", result.Summary?.Account?.Label); Assert.Null(result.Summary?.Plan);
    }

    [Theory] [InlineData("APPDATA")] [InlineData("CURSOR_CONFIG_DIR")] [InlineData("XDG_CONFIG_HOME")]
    public void NativeAgentDirectoryOverridesReadOnlyTheSelectedCurrentLocation(string name)
    {
        Agent(); var destination = Path.Combine(root, "custom"); environment[name] = destination;
        var selected = name == "APPDATA" ? Path.Combine(destination, "Cursor", "auth.json")
            : Path.Combine(destination, name == "XDG_CONFIG_HOME" ? "cursor" : "", "cli-config.json");
        Write(selected, File.ReadAllText(name == "APPDATA" ? Auth : Config));
        Assert.NotNull(Read().Login);
        File.Delete(selected);
        if (name == "APPDATA") Assert.Null(Read().Login);
        else { Assert.NotNull(Read().Login); Assert.Null(Read().Summary?.Account); }
    }

    [Fact]
    public void ConfigOverrideWinsOverXdgAndBlankOverridesUseDefaults()
    {
        Agent(); environment["CURSOR_CONFIG_DIR"] = " "; environment["XDG_CONFIG_HOME"] = "\t";
        Assert.Equal("agent@example.invalid", Read().Summary?.Account?.Label);
        environment["CURSOR_CONFIG_DIR"] = Path.GetDirectoryName(Config);
        environment["XDG_CONFIG_HOME"] = Path.Combine(root, "other");
        Assert.Equal("agent@example.invalid", Read().Summary?.Account?.Label);
    }

    [Theory] [InlineData("memory", false)] [InlineData("file", true)] [InlineData(null, true)] [InlineData("unknown", true)]
    public void MemoryCredentialStoreCannotResurrectAStoredSession(string? store, bool available)
    {
        Agent(); environment["AGENT_CLI_CREDENTIAL_STORE"] = store;
        Assert.Equal(available, Read().Login is not null);
        if (!available) Assert.Null(Read().Summary);
        Editor(); Assert.Equal("Cursor", Read().Login?.Account.Source);
    }

    [Theory] [InlineData(1799999999, false)] [InlineData(1800000000, false)] [InlineData(1800000001, true)]
    public void ExpiryDisablesRequestsWithoutErasingAccountMetadata(long exp, bool active)
    {
        Agent(Jwt(expiration: exp)); var result = Read();
        Assert.Equal(active, result.Login is not null); Assert.Equal("agent@example.invalid", result.Summary?.Account?.Label);
    }

    [Theory] [InlineData("{}")] [InlineData("{\"refreshToken\":\"refresh-only\"}")] [InlineData("{\"apiKey\":\"api-key-only\"}")]
    public void MetadataAndOtherCredentialKindsNeverAuthorizeRequests(string json)
    {
        Agent(); Write(Auth, json); var result = Read();
        Assert.Null(result.Login); Assert.Equal("agent@example.invalid", result.Summary?.Account?.Label);
    }

    [Theory] [InlineData("{}")] [InlineData("not JSON")] [InlineData("null")] [InlineData("{\"authInfo\":[]}")]
    public void JwtSubjectAllowsQuotaWithoutUnboundDisplayMetadata(string config)
    {
        Agent(); Write(Config, config); var result = Read();
        Assert.StartsWith("WorkosCursorSessionToken=agent-owner::", result.Login!.Credential, StringComparison.Ordinal); Assert.Null(result.Summary?.Account);
    }

    [Fact]
    public void ConflictingMetadataCannotBindAnOldEmailToNewQuota()
    {
        Agent(Jwt("new-owner")); var result = Read();
        Assert.StartsWith("WorkosCursorSessionToken=new-owner::", result.Login!.Credential, StringComparison.Ordinal);
        Assert.Null(result.Login.Account.Label); Assert.Null(result.Summary?.Account);
        Agent(Jwt("new-owner"), "new@example.invalid", "new-owner");
        Assert.Equal("new@example.invalid", Read().Summary?.Account?.Label);
        Assert.NotEqual(result.Summary!.Version, Read().Summary!.Version);
    }

    [Theory] [InlineData("auth", "numeric", "auth")] [InlineData(null, "123", "123")] [InlineData(null, null, null)]
    public void OpaqueTokenUsesAuthIdThenUserId(string? authId, string? userId, string? expected)
    {
        Agent("opaque-token", authId: authId, userId: userId);
        Assert.Equal(expected is null ? null : "WorkosCursorSessionToken=" + expected + "::opaque-token", Read().Login?.Credential);
        Agent("opaque-token", authId: null, userId: 123);
        Assert.Equal("WorkosCursorSessionToken=123::opaque-token", Read().Login?.Credential);
    }

    [Theory] [InlineData("token; other=value")] [InlineData("token,other")] [InlineData("token\r\nheader")]
    [InlineData("token value")] [InlineData("token\"value")] [InlineData("token\\value")] [InlineData("토큰")]
    public void UnsafeCookieTokensNeverAuthorizeARequest(string token)
    { Agent(token); Assert.Null(Read().Login); }

    [Theory] [InlineData("owner; other=value")] [InlineData("owner::other")] [InlineData("owner\r\nheader")]
    [InlineData("owner value")] [InlineData("owner\"value")] [InlineData("owner\\value")]
    public void UnsafeSubjectsNeverFormACookie(string subject)
    { Agent("opaque", authId: subject); Assert.Null(Read().Login); }

    [Theory] [InlineData("auth")] [InlineData("config")]
    public void InvalidUtf8AndOversizedFilesAreRejected(string file)
    {
        Agent(); var path = file == "auth" ? Auth : Config;
        File.WriteAllBytes(path, [0xff, 0xfe, 0xff]);
        if (file == "auth") Assert.Null(Read().Login); else Assert.Null(Read().Summary?.Account);
        Write(path, new string('x', 262145));
        if (file == "auth") Assert.Null(Read().Login); else Assert.Null(Read().Summary?.Account);
    }

    [Fact]
    public void DuplicateCredentialOrIdentityFieldsDoNotSelectAnAmbiguousOwner()
    {
        Agent(); Write(Auth, "{\"accessToken\":\"first\",\"accessToken\":\"second\"}"); Assert.Null(Read().Login);
        Agent(); Write(Config, "{\"authInfo\":{\"email\":\"a\",\"email\":\"b\",\"authId\":\"agent-owner\"}}");
        Assert.NotNull(Read().Login); Assert.Null(Read().Summary?.Account);
    }

    [Theory] [InlineData("auth")] [InlineData("config")]
    public void AChangingPairIsNeverReturned(string changed)
    {
        Agent(); var counts = new Dictionary<string, int>();
        string? ReadChanging(string path)
        {
            counts[path] = counts.GetValueOrDefault(path) + 1;
            if (counts[path] == 2 && path == (changed == "auth" ? Auth : Config)) return "{}";
            return File.ReadAllText(path);
        }
        var result = CursorAuthentication.ReadAgentFiles(Auth, Config, Now, ReadChanging);
        Assert.Null(result.Login); Assert.Null(result.Summary);
    }

    [Theory] [InlineData("token")] [InlineData("label")] [InlineData("owner")]
    public void EveryAccountRelevantChangeInvalidatesOwnership(string change)
    {
        Agent(); var first = Read();
        Agent(change == "token" ? Jwt(expiration: 1800007200) : change == "owner" ? Jwt("replacement") : Jwt(),
            change == "label" ? "changed@example.invalid" : "agent@example.invalid", change == "owner" ? "replacement" : "agent-owner");
        Assert.NotEqual(first.Summary!.Version, Read().Summary!.Version);
    }

    [Fact]
    public void MissingStoresDoNotCreateFilesAndRelativeOverridesAreNotRead()
    {
        Assert.Null(Read().Login); Assert.Null(Read().Summary); Assert.Empty(Directory.GetFileSystemEntries(root));
        Agent(); environment["APPDATA"] = "relative"; environment["CURSOR_CONFIG_DIR"] = "relative";
        Assert.Null(Read().Login); Assert.Null(Read().Summary);
        environment["APPDATA"] = @"\\untrusted.invalid\share";
        Assert.Null(Read().Login);
    }

    [Theory] [InlineData(@"\\untrusted.invalid\share")] [InlineData("//untrusted.invalid/share")]
    [InlineData(@"/\untrusted.invalid/share")] [InlineData(@"\\?\UNC\untrusted.invalid\share")]
    public void NetworkAndDevicePathsAreRejectedBeforeDiscovery(string path)
    {
        Assert.False(CursorAuthentication.IsLocalLoginPath(path));
        Assert.True(CursorAuthentication.IsLocalLoginPath(Auth));
        environment["APPDATA"] = path; environment["CURSOR_CONFIG_DIR"] = path;
        var result = Read(); Assert.Null(result.Login); Assert.Null(result.Summary);
    }

    [Fact]
    public void LegacyGuardedReadsKeepBomSupportWhileAgentFilesRequireUtf8()
    {
        Agent(); var text = File.ReadAllText(Auth); File.WriteAllText(Auth, text, Encoding.Unicode);
        Assert.Equal(text, GuardedFile.Read(Auth)); Assert.Throws<DecoderFallbackException>(() => GuardedFile.ReadUtf8(Auth));
        Assert.Null(Read().Login);
        File.WriteAllText(Auth, text, new UTF8Encoding(true)); Assert.NotNull(Read().Login);
    }

    [Theory] [InlineData("auth")] [InlineData("config")]
    public void ReparseAncestorsCannotSelectAnotherLoginLocation(string file)
    {
        Agent(); var link = Path.Combine(root, "redirect");
        var target = file == "auth" ? Roaming : Path.GetDirectoryName(Config)!;
        if (OperatingSystem.IsWindows()) CreateJunction(link, target);
        else Directory.CreateSymbolicLink(link, target);
        try
        {
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            environment[file == "auth" ? "APPDATA" : "CURSOR_CONFIG_DIR"] = link;
            if (file == "auth") Assert.Null(Read().Login);
            else { Assert.NotNull(Read().Login); Assert.Null(Read().Summary?.Account); }
        }
        finally { Directory.Delete(link); }
    }

    [Theory] [InlineData(65536, true)] [InlineData(65537, false)]
    public void TokenLengthIsBounded(int length, bool accepted)
    { Agent(new string('a', length)); Assert.Equal(accepted, Read().Login is not null); }

    [Theory] [InlineData(4096, true)] [InlineData(4097, false)]
    public void SubjectLengthIsBounded(int length, bool accepted)
    { Agent("opaque", authId: new string('a', length)); Assert.Equal(accepted, Read().Login is not null); }

    [Fact]
    public void InvalidOptionalEmailDoesNotPreventValidQuotaOrBecomeVisible()
    {
        foreach (var label in new[] { new string('a', 257), "email\r\ncontrol" })
        {
            Agent(email: label); var result = Read(); Assert.NotNull(result.Login);
            Assert.Null(result.Summary?.Account?.Label); Assert.Equal("cursor-agent", result.Summary?.Account?.Source);
        }
    }

    private static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        using var handle = CreateFileW(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var display = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var data = new byte[16 + substitute.Length + 2 + display.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xA0000003);
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

    public void Dispose() => Directory.Delete(root, true);
}
