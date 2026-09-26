using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;
namespace CodeRim.Core.Tests;
public sealed class LocalStateDatabaseTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "editor-state-" + Guid.NewGuid().ToString("N"));
    public LocalStateDatabaseTests() => Directory.CreateDirectory(directory);
    private const string Quota = """{"planName":"Pro","quotaUsage":{"dailyRemainingPercent":0,"weeklyRemainingPercent":100,"dailyResetAtUnix":1800000000}}""";
    private SqliteConnection Create(object value, string name = "state.vscdb")
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, name), Pooling = false }.ToString());
        connection.Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE ItemTable(key TEXT PRIMARY KEY,value BLOB); INSERT INTO ItemTable VALUES('windsurf.settings.cachedPlanInfo',$value)";
        setup.Parameters.AddWithValue("$value", value); setup.ExecuteNonQuery();
        return connection;
    }
    private static byte[] SharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }
    [Theory]
    [InlineData("text")]
    [InlineData("utf8")]
    [InlineData("utf16")]
    public void ReadsCommittedWalAndPreservesTheDatabaseWithTextOrBlob(string encoding)
    {
        using var connection = Create(encoding switch { "utf8" => (object)Encoding.UTF8.GetBytes(Quota), "utf16" => Encoding.Unicode.GetBytes(Quota), _ => Quota });
        var path = Path.Combine(directory, "state.vscdb");
        var db = SharedBytes(path); var wal = SharedBytes(path + "-wal");
        var reading = WindsurfLocalUsage.Read(path);
        Assert.Equal(ReadingState.Stale, reading.State); Assert.Null(reading.UpdatedAt);
        Assert.Equal("Pro", reading.Plan); Assert.Equal(100, reading.Windows[0].UsedPercent); Assert.Equal(0, reading.Windows[1].UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), reading.Windows[0].ResetsAt);
        Assert.Equal(db, SharedBytes(path)); Assert.Equal(wal, SharedBytes(path + "-wal"));
    }
    [Fact]
    public void LegacyMessageAndFlowCountsKeepTheirUnits()
    {
        using var document = JsonDocument.Parse("""{"usage":{"messages":100,"remainingMessages":70,"flowActions":50,"usedFlowActions":20},"endTimestamp":1800000000000}""");
        var reading = WindsurfLocalUsage.Parse(document.RootElement);
        Assert.Equal(30, reading.Windows[0].UsedPercent); Assert.Equal(30, reading.Windows[0].UsedCount); Assert.Equal("messages", reading.Windows[0].Unit);
        Assert.Equal("messages", reading.Windows[0].Id); Assert.Equal("Messages", reading.Windows[0].Name);
        Assert.Equal("flowActions", reading.Windows[1].Id); Assert.Equal("Flow actions", reading.Windows[1].Name);
        Assert.All(reading.Windows, window => { Assert.Null(window.ResetsAt); Assert.Equal(0, window.DurationMinutes); });
        Assert.Equal(40, reading.Windows[1].UsedPercent); Assert.Equal("flow actions", reading.Windows[1].Unit);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"quotaUsage":{"dailyRemainingPercent":-1,"weeklyRemainingPercent":101},"usage":{"messages":0}}""")]
    public void UnknownOrInvalidQuotasDoNotBecomeZeroUsage(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Empty(WindsurfLocalUsage.Parse(document.RootElement).Windows);
    }
    [Fact]
    public void OversizedValueIsRejectedBeforeItIsReturnedToTheClient()
    {
        using var connection = Create(new string('x', 262145));
        var path = Path.Combine(directory, "state.vscdb");
        Assert.Throws<InvalidDataException>(() => LocalStateDatabase.Read(path, "windsurf.settings.cachedPlanInfo"));
        Assert.Empty(WindsurfLocalUsage.Read(path).Windows);
    }
    [Theory]
    [InlineData("cursorAuth/cachedEmail")]
    [InlineData("cursorAuth/stripeMembershipType")]
    public void OversizedOptionalDisplayValuePreservesTheSameSnapshotCredentials(string oversizedKey)
    {
        using var connection = Create(Quota);
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO ItemTable VALUES('cursorAuth/accessToken','fixture-token'),('cursorAuth/stripeMembershipAuthId','fixture-owner'),('cursorAuth/cachedEmail','fixture@example.invalid'),('cursorAuth/stripeMembershipType','pro'); UPDATE ItemTable SET value=$value WHERE key=$key";
        insert.Parameters.AddWithValue("$value", new string('x', 262145)); insert.Parameters.AddWithValue("$key", oversizedKey); insert.ExecuteNonQuery();
        var path = Path.Combine(directory, "state.vscdb");
        var db = SharedBytes(path); var wal = SharedBytes(path + "-wal");
        string[] required = ["cursorAuth/accessToken", "cursorAuth/stripeMembershipAuthId"];
        string[] optional = ["cursorAuth/cachedEmail", "cursorAuth/stripeMembershipType"];
        var values = LocalStateDatabase.ReadWithOptionalValues(path, required, optional);
        Assert.DoesNotContain(oversizedKey, values.Keys);
        var login = NativeProviderLogin.Cursor(values);
        Assert.NotNull(login); Assert.Equal("WorkosCursorSessionToken=fixture-owner::fixture-token", login.Credential);
        Assert.Equal(oversizedKey == optional[0] ? null : "fixture@example.invalid", login.Account.Label);
        Assert.Equal(oversizedKey == optional[1] ? null : "pro", login.Plan);
        Assert.Equal(db, SharedBytes(path)); Assert.Equal(wal, SharedBytes(path + "-wal"));
        Assert.Throws<InvalidDataException>(() => LocalStateDatabase.Read(path, [..required, ..optional]));
        Assert.Throws<InvalidDataException>(() => LocalStateDatabase.ReadWithOptionalValues(path, [oversizedKey], oversizedKey));
    }
    [Fact]
    public void MissingFilesAndInvalidEncodingFailWithoutCreatingData()
    {
        var missing = Path.Combine(directory, "missing.vscdb");
        Assert.Equal(ReadingState.Unavailable, WindsurfLocalUsage.Read(missing).State);
        Assert.False(File.Exists(missing));
        using var connection = Create(new byte[] { 255, 255, 255 });
        Assert.Equal(ReadingState.Error, WindsurfLocalUsage.Read(Path.Combine(directory, "state.vscdb")).State);
    }
    [Fact]
    public void ExactKeySelectionDoesNotReadOtherCredentials()
    {
        using var connection = Create(Quota);
        using var insert = connection.CreateCommand(); insert.CommandText = "INSERT INTO ItemTable VALUES('unrelated-credential','synthetic-sensitive')";
        insert.ExecuteNonQuery();
        var values = LocalStateDatabase.Read(Path.Combine(directory, "state.vscdb"), "windsurf.settings.cachedPlanInfo");
        Assert.Single(values); Assert.DoesNotContain("unrelated-credential", values.Keys);
    }
    [Theory]
    [InlineData("CREATE VIEW ItemTable AS SELECT 'windsurf.settings.cachedPlanInfo' AS key, (WITH RECURSIVE c(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM c WHERE x<100000000) SELECT sum(x) FROM c) AS value")]
    [InlineData("CREATE TABLE ItemTable(key TEXT, value TEXT GENERATED ALWAYS AS (hex(zeroblob(100000000))) VIRTUAL)")]
    [InlineData("CREATE TABLE ItemTable(source TEXT, key TEXT GENERATED ALWAYS AS (source) STORED, value TEXT)")]
    [InlineData("CREATE VIRTUAL TABLE ItemTable USING fts5(key,value)")]
    public void ExecutableSchemaIsRejectedBeforeValueEvaluation(string schema)
    {
        var path = Path.Combine(directory, "schema.vscdb");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); using var setup = connection.CreateCommand(); setup.CommandText = schema; setup.ExecuteNonQuery();
        Assert.Throws<InvalidDataException>(() => LocalStateDatabase.Read(path, "windsurf.settings.cachedPlanInfo"));
        Assert.Equal(ReadingState.Unavailable, WindsurfLocalUsage.Read(path).State);
    }
    [Fact]
    public void UnindexedLargeTableHasAnExecutionBudget()
    {
        var path = Path.Combine(directory, "scan.vscdb");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); using var setup = connection.CreateCommand();
        setup.CommandText = "CREATE TABLE ItemTable(key TEXT,value TEXT); WITH RECURSIVE c(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM c WHERE x<400000) INSERT INTO ItemTable SELECT 'unrelated-'||x,'value' FROM c";
        setup.ExecuteNonQuery();
        var error = Assert.Throws<SqliteException>(() => LocalStateDatabase.Read(path, "windsurf.settings.cachedPlanInfo"));
        Assert.Equal(9, error.SqliteErrorCode); // SQLITE_INTERRUPT from the VM budget.
    }
    public void Dispose() => Directory.Delete(directory, true);
}
