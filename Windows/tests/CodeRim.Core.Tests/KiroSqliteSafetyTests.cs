using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace CodeRim.Core.Tests;

public sealed class KiroSqliteSafetyTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "kiro-safety-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(directory, "data.sqlite3");
    private const string Tables = "CREATE TABLE auth_kv(key TEXT,value TEXT); CREATE TABLE state(key TEXT,value TEXT);";
    private const string Records = """INSERT INTO auth_kv VALUES('kirocli:odic:token','{"access_token":"fixture"}'); INSERT INTO state VALUES('api.codewhisperer.profile','{"arn":"arn:aws:codewhisperer:us-east-1:123:profile/example"}');""";
    public KiroSqliteSafetyTests() => Directory.CreateDirectory(directory);
    private SqliteConnection Create(string schema)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open(); using var setup = connection.CreateCommand();
        setup.CommandText = "PRAGMA journal_mode=WAL;" + schema; setup.ExecuteNonQuery(); return connection;
    }
    [Theory]
    [InlineData("CREATE VIEW auth_kv AS SELECT 'kirocli:odic:token' AS key,(WITH RECURSIVE c(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM c WHERE x<100000000) SELECT sum(x) FROM c) AS value; CREATE TABLE state(key TEXT,value TEXT);")]
    [InlineData("CREATE TABLE auth_kv(key TEXT,value TEXT); CREATE VIEW state AS SELECT 'api.codewhisperer.profile' AS key,hex(zeroblob(100000000)) AS value;")]
    [InlineData("CREATE TABLE auth_kv(key TEXT,value TEXT GENERATED ALWAYS AS (hex(zeroblob(100000000))) VIRTUAL); CREATE TABLE state(key TEXT,value TEXT);")]
    [InlineData("CREATE TABLE auth_kv(key TEXT,value TEXT); CREATE VIRTUAL TABLE state USING fts5(key,value);")]
    public void RejectsExecutableAuthenticationSchemas(string schema)
    {
        using var connection = Create(schema);
        Assert.Throws<InvalidDataException>(() => KiroAuthentication.Read(Database));
    }
    [Fact]
    public void UnindexedAuthenticationScanHasAVmBudget()
    {
        using var connection = Create(Tables); using var insert = connection.CreateCommand();
        insert.CommandText = "WITH RECURSIVE c(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM c WHERE x<400000) INSERT INTO auth_kv SELECT 'unrelated','value' FROM c";
        insert.ExecuteNonQuery();
        Assert.Equal(9, Assert.Throws<SqliteException>(() => KiroAuthentication.Read(Database)).SqliteErrorCode);
    }
    [Fact]
    public void OversizedAuthenticationDoesNotProduceCredentials()
    {
        using var connection = Create(Tables + Records); using var update = connection.CreateCommand();
        update.CommandText = "UPDATE auth_kv SET value=$value"; update.Parameters.AddWithValue("$value", new string('x', 262145)); update.ExecuteNonQuery();
        Assert.Null(KiroAuthentication.Read(Database));
    }
    [Fact]
    public void ReadsCommittedWalAndIgnoresUncommittedAccountChanges()
    {
        using var connection = Create(Tables + Records);
        byte[] Snapshot(string suffix)
        {
            using var stream = new FileStream(Database + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
        }
        var database = Snapshot(""); var wal = Snapshot("-wal");
        using var transaction = connection.BeginTransaction(); using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = """UPDATE auth_kv SET value='{"access_token":"not-committed"}'; UPDATE state SET value='{"arn":"not-committed"}';"""; update.ExecuteNonQuery();
        using var auth = JsonDocument.Parse(KiroAuthentication.Read(Database)!);
        Assert.Equal("fixture", auth.RootElement.GetProperty("access_token").GetString());
        Assert.Equal("arn:aws:codewhisperer:us-east-1:123:profile/example", auth.RootElement.GetProperty("profileArn").GetString());
        Assert.Equal(database, Snapshot("")); Assert.Equal(wal, Snapshot("-wal"));
    }
    public void Dispose() => Directory.Delete(directory, true);
}
