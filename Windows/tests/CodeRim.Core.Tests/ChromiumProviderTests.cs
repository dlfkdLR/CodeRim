using System.Buffers.Binary;
using System.Net;
using System.Text;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Tests;

public sealed class ChromiumProviderTests : IDisposable
{
    private const string Token = "synthetic-token-one-1234567890";
    private const string Other = "synthetic-token-two-1234567890";
    private const string Origin = "https://platform.deepseek.com";
    private readonly string profile = Path.Combine(AppContext.BaseDirectory, "TestResults", "chromium-provider-" + Guid.NewGuid().ToString("N"));
    public ChromiumProviderTests() => Directory.CreateDirectory(profile);
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(profile, true); }
    [Theory]
    [InlineData("synthetic-token-one-1234567890")]
    [InlineData("'synthetic-token-one-1234567890'")]
    [InlineData("\"synthetic-token-one-1234567890\"")]
    [InlineData("{\"value\":\"synthetic-token-one-1234567890\"}")]
    [InlineData("{\"token\":\"synthetic-token-one-1234567890\",\"value\":\"synthetic-token-one-1234567890\"}")]
    public void DeepSeekCurrentTypedFormats(string text) => Assert.Equal(Token, ChromiumProviderAuthentication.DeepSeekToken(text));
    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"value\":1}")]
    [InlineData("{\"value\":\"synthetic-token-one-1234567890\",\"value\":\"synthetic-token-one-1234567890\"}")]
    [InlineData("{\"value\":\"synthetic-token-one-1234567890\",\"token\":\"synthetic-token-two-1234567890\"}")]
    [InlineData("{\"unrelated\":\"synthetic-token-one-1234567890\"}")]
    [InlineData("synthetic-token-one-1234567890\nInjected: true")]
    public void DeepSeekRejectsMalformedOrAmbiguous(string text) => Assert.ThrowsAny<Exception>(() => ChromiumProviderAuthentication.DeepSeekToken(text));
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
    private static byte[] U32(uint value) { var result = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(result, value); return result; }
    private static byte[] U64(ulong value) { var result = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(result, value); return result; }
    private static byte[] Var(ulong value) { var result = new List<byte>(); while (value >= 128) { result.Add((byte)(value | 128)); value >>= 7; } result.Add((byte)value); return result.ToArray(); }
    private static byte[] Sized(byte[] bytes) => Cat(Var((ulong)bytes.Length), bytes);
    private static byte[] Log(byte[] value)
    {
        uint crc = uint.MaxValue; foreach (var b in Cat([1], value)) { crc ^= b; for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0x82f63b78); }
        crc = ~crc; var length = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)value.Length));
        return Cat(U32(unchecked(((crc >> 15) | (crc << 17)) + 0xa282ead8)), length, [1], value);
    }
    private void Storage(string origin, params (string Key, string Value)[] values)
    {
        var directory = Path.Combine(profile, "Local Storage", "leveldb"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "CURRENT"), "MANIFEST-000004\n");
        File.WriteAllBytes(Path.Combine(directory, "MANIFEST-000004"), Log(Cat([1], Sized("leveldb.BytewiseComparator"u8.ToArray()), [2, 3, 3, 100, 4, 100])));
        var entries = Cat([1], Sized("VERSION"u8.ToArray()), Sized("1"u8.ToArray()), values.SelectMany(pair => Cat([1], Sized(Cat(Encoding.UTF8.GetBytes("_" + origin + "\0"), [1], Encoding.ASCII.GetBytes(pair.Key))), Sized(Cat([1], Encoding.UTF8.GetBytes(pair.Value))))).ToArray());
        File.WriteAllBytes(Path.Combine(directory, "000003.log"), Log(Cat(U64(1), U32((uint)values.Length + 1), entries)));
    }
    private ChromiumProviderCredential Read(string id, string region, string origin) => ChromiumProviderAuthentication.Read(id, region, profile, origin, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    [Fact] public void ActualCurrentReaderFeedsDeepSeekAndFactoryWithoutOtherOrigins()
    {
        Storage(Origin, ("userToken", "{\"value\":\"" + Token + "\"}")); var value = Read("deepseek", "default", Origin);
        Assert.Equal(Token, value.Secret); Assert.Equal(value, ChromiumProviderCredential.Parse(value.Serialize())); Assert.DoesNotContain(Token, value.ToString(), StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => Read("factory", "default", "https://app.factory.ai"));
        Storage("https://app.factory.ai", ("workos:access-token", Token), ("workos:refresh-token", Other));
        var factory = FactoryWorkOsProfile.Parse(Read("factory", "default", "https://app.factory.ai").Secret!);
        Assert.Equal(Token, factory.AccessToken); Assert.Equal(Other, factory.RefreshToken);
        Assert.ThrowsAny<Exception>(() => Read("factory", "default", "https://auth.factory.ai"));
    }
    [Theory] [InlineData("http://platform.deepseek.com")] [InlineData("https://platform.deepseek.com:443")] [InlineData("https://evil.test")]
    public void ProviderOriginIsPinnedBeforeReading(string origin) => Assert.ThrowsAny<Exception>(() => Read("deepseek", "default", origin));
    [Fact] public void WrapperRejectsDuplicateAndWrongRegionWithoutLookingAtProfile()
    {
        var value = new ChromiumProviderCredential("deepseek", "default", profile, Origin, Token);
        Assert.ThrowsAny<Exception>(() => ChromiumProviderCredential.Parse(value.Serialize().Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal)));
        Assert.ThrowsAny<Exception>(() => ChromiumProviderCredential.Parse((value with { Region = "cn" }).Serialize()));
        Assert.ThrowsAny<Exception>(() => ChromiumProviderCredential.Parse((value with { Cookies = "other" }).Serialize()));
    }
    private string Cookies(string? version = "24", string? schema = null)
    {
        var path = Path.Combine(profile, "Cookies");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); connection.Open();
        using var query = connection.CreateCommand(); query.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT); INSERT INTO meta VALUES('version',$version); " + (schema ?? "CREATE TABLE cookies(host_key TEXT,name TEXT,value TEXT,encrypted_value BLOB,path TEXT,is_secure INTEGER,expires_utc INTEGER,has_expires INTEGER,top_frame_site_key TEXT,has_cross_site_ancestor INTEGER);"); query.Parameters.AddWithValue("$version", version ?? ""); query.ExecuteNonQuery(); return path;
    }
    private static void AddCookie(string path, string name, string value, string host = ".minimax.io", string cookiePath = "/", byte[]? encrypted = null, string partition = "", long expires = 0)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); connection.Open();
        using var query = connection.CreateCommand(); query.CommandText = "INSERT INTO cookies VALUES($host,$name,$value,$encrypted,$path,1,$expires,$has,$partition,0)";
        query.Parameters.AddWithValue("$host", host); query.Parameters.AddWithValue("$name", name); query.Parameters.AddWithValue("$value", value); query.Parameters.AddWithValue("$encrypted", encrypted ?? []); query.Parameters.AddWithValue("$path", cookiePath); query.Parameters.AddWithValue("$expires", expires); query.Parameters.AddWithValue("$has", expires == 0 ? 0 : 1); query.Parameters.AddWithValue("$partition", partition); query.ExecuteNonQuery();
    }
    private BrowserCookieJar ReadCookies() => ChromiumPlaintextCookies.Read(profile, "minimax.io", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    [Fact] public void PlaintextCookiesAreScopedAndSourceBytesUnchanged()
    {
        var path = Cookies(); AddCookie(path, "HERTZ-SESSION", Token); AddCookie(path, "pathOnly", "ok", "platform.minimax.io", "/account");
        AddCookie(path, "expired", "bad", expires: 11644473600000001); AddCookie(path, "partitioned", "bad", partition: "https://evil.test"); AddCookie(path, "foreign", "bad", host: "evil.test");
        var before = Directory.GetFiles(profile).ToDictionary(x => x, File.ReadAllBytes); var jar = ReadCookies();
        Assert.Equal("HERTZ-SESSION=" + Token, jar.Header(new Uri("https://platform.minimax.io/"), DateTimeOffset.UtcNow));
        Assert.Contains("pathOnly=ok", jar.Header(new Uri("https://platform.minimax.io/account"), DateTimeOffset.UtcNow));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(profile).Order()); foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }
    [Theory] [InlineData("23")] [InlineData("25")] [InlineData("")]
    public void UnsupportedCookieSchemaIsExplicit(string version) { Cookies(version); Assert.Throws<InvalidDataException>(() => ReadCookies()); }
    [Fact] public void NeverDecryptsProtectedCookies() { var path = Cookies(); AddCookie(path, "HERTZ-SESSION", "", encrypted: [1, 2, 3]); Assert.Throws<InvalidDataException>(() => ReadCookies()); }
    [Fact] public void RejectsUncheckpointedDatabase() { var path = Cookies(); File.WriteAllBytes(path + "-wal", [1]); Assert.Throws<IOException>(() => ReadCookies()); }
    [Fact] public void RejectsSqliteViewsBeforeQuery() { Cookies(schema: "CREATE VIEW cookies AS SELECT 1 host_key;"); Assert.Throws<InvalidDataException>(() => ReadCookies()); }
    [Fact] public void RejectsOversizeAndDuplicateCookieRows()
    { var path = Cookies(); AddCookie(path, "HERTZ-SESSION", new string('x', 32769)); Assert.Throws<InvalidDataException>(() => ReadCookies()); }
    [Fact] public void RejectsDuplicateTuple() { var path = Cookies(); AddCookie(path, "HERTZ-SESSION", Token); AddCookie(path, "HERTZ-SESSION", Other); Assert.Throws<InvalidDataException>(() => ReadCookies()); }
    [Fact] public void CancellationPrecedesAnySourceRead() { using var stop = new CancellationTokenSource(); stop.Cancel(); Assert.Throws<OperationCanceledException>(() => ChromiumPlaintextCookies.Read(profile, "minimax.io", DateTimeOffset.UtcNow, stop.Token)); }
    [Theory] [InlineData("global", "minimax.io")] [InlineData("cn", "minimaxi.com")]
    public void MiniMaxRequiresSameProfileGroupForDistinctLocalStorageBearer(string region, string domain)
    {
        var path = Cookies(); AddCookie(path, "HERTZ-SESSION", Token, "." + domain); AddCookie(path, "minimax_group_id_v2", "123", "." + domain);
        var origin = "https://platform." + domain;
        Storage(origin, ("access_token", Other), ("group_id", "123")); var result = Read("minimax", region, origin);
        Assert.Equal(Other, result.MiniMax().Bearer); Assert.Equal("123", result.Group);
        Storage(origin, ("access_token", Other), ("group_id", "456")); Assert.Throws<InvalidDataException>(() => Read("minimax", region, origin));
    }
    [Fact] public void MiniMaxDoesNotBorrowCookiesFromAnotherProfile()
    { Storage("https://platform.minimax.io", ("access_token", Other), ("group_id", "123")); Assert.ThrowsAny<Exception>(() => Read("minimax", "global", "https://platform.minimax.io")); }
    [Theory] [InlineData("{\"group_id\":123,\"token\":\"synthetic-token-one-1234567890\"}")]
    [InlineData("{\"auth\":\"{\\\"group_id\\\":\\\"123\\\",\\\"token\\\":\\\"synthetic-token-one-1234567890\\\"}\"}")]
    public void MiniMaxTypedPersistedAuthHandlesNumericGroupAndNestedJson(string text)
    { var pair = ChromiumProviderAuthentication.MiniMaxFields(new Dictionary<string,string> { ["persist:root"] = text }); Assert.Equal(Token, pair.Token); Assert.Equal("123", pair.Group); }
    [Fact] public void DistinctBearerDoesNotBorrowItsOnlyGroupFromCookies()
    {
        var path = Cookies(); AddCookie(path, "HERTZ-SESSION", Token); AddCookie(path, "minimax_group_id_v2", "123");
        Storage("https://platform.minimax.io", ("access_token", Other));
        Assert.Throws<InvalidDataException>(() => Read("minimax", "global", "https://platform.minimax.io"));
    }
    [Theory] [InlineData("{\"group_id\":1.5}")] [InlineData("{\"group_id\":-1}")]
    [InlineData("{\"group_id\":1,\"GroupID\":2}")] [InlineData("{\"token\":null}")]
    [InlineData("{\"auth\":[]}")]
    public void MiniMaxTypedAuthRejectsConflictingOrMalformedKnownFields(string text)
    { Assert.ThrowsAny<Exception>(() => ChromiumProviderAuthentication.MiniMaxFields(new Dictionary<string,string> { ["persist:root"] = text })); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request)); }
    [Fact] public async Task ValidatedTupleReachesActualMiniMaxAndMutationCannotClaimItsProof()
    {
        var path = Cookies(); AddCookie(path, "HERTZ-SESSION", Token); AddCookie(path, "minimax_group_id_v2", "123");
        Storage("https://platform.minimax.io", ("access_token", Other), ("group_id", "123")); var value = Read("minimax", "global", "https://platform.minimax.io").MiniMax();
        var calls = 0; using var provider = new NativeProviders(new Handler(request => { calls++; if (calls == 1) Assert.Equal(Other, request.Headers.Authorization?.Parameter); return new(HttpStatusCode.OK) { Content = new StringContent("""{"model_remains":[{"model_name":"general","current_interval_remaining_percent":96}]}""") }; }));
        Assert.Contains((await provider.FetchMiniMaxWebAsync(value, TestContext.Current.CancellationToken)).State, new[] { ReadingState.Ready, ReadingState.Partial });
        var before = calls; Assert.Equal(ReadingState.NeedsAuth, (await provider.FetchMiniMaxWebAsync(value with { Bearer = "synthetic-attacker-123456789" }, TestContext.Current.CancellationToken)).State); Assert.Equal(before, calls);
    }
    [Theory] [InlineData("global", "minimax.io")] [InlineData("cn", "minimaxi.com")]
    public async Task ImportedMiniMaxUsesBothRegionalHostsWithTheSameSessionAndDistinctBearer(string region, string domain)
    {
        var jar = new BrowserCookieJar(new[] { new BrowserCookie("HERTZ-SESSION", Token, "." + domain, "/", true, false, 0),
            new BrowserCookie("minimax_group_id_v2", "123", "." + domain, "/", true, false, 0) }, [domain]);
        var credential = new ChromiumProviderCredential("minimax", region, profile, "https://platform." + domain, Other, jar.Serialize(), "123");
        var requests = new List<string>();
        using var provider = new NativeProviders(new Handler(request =>
        {
            var uri = request.RequestUri!; requests.Add(uri.PathAndQuery);
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("https", uri.Scheme);
            var cookie = request.Headers.GetValues("Cookie").Single();
            Assert.Contains("HERTZ-SESSION=" + Token, cookie, StringComparison.Ordinal);
            Assert.Contains("minimax_group_id_v2=123", cookie, StringComparison.Ordinal);
            string body;
            if (uri.Host == "www." + domain)
            {
                Assert.Equal("/v1/api/openplatform/charge/combo/cycle_audio_resource_package?biz_line=2&cycle_type=3&resource_package_type=7", uri.PathAndQuery);
                Assert.Null(request.Headers.Authorization); Assert.Equal("123", request.Headers.GetValues("x-group-id").Single());
                body = """{"data":{"current_subscribe":{"title":"Synthetic Coding Plan"}}}""";
            }
            else
            {
                Assert.Equal("platform." + domain, uri.Host); Assert.Equal(Other, request.Headers.Authorization?.Parameter);
                if (uri.AbsolutePath == "/account/amount")
                {
                    Assert.Equal("/account/amount?page=1&limit=100&aggregate=false", uri.PathAndQuery);
                    body = """{"charge_records":[],"total_cnt":0}""";
                }
                else
                {
                    Assert.Equal("/user-center/payment/coding-plan?cycle_type=3", uri.PathAndQuery);
                    body = """{"model_remains":[{"model_name":"general","current_interval_remaining_percent":96}]}""";
                }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var reading = await provider.FetchMiniMaxWebAsync(credential.MiniMax(), TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Equal("Synthetic Coding Plan", reading.Plan);
        Assert.Equal(4, reading.Headline!.UsedPercent); Assert.Equal(3, requests.Count); Assert.Equal(3, requests.Distinct().Count());
    }
}
