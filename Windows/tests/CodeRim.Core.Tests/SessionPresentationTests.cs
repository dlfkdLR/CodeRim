using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Tests;

public sealed class SessionPresentationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    [Theory]
    [InlineData("busy", true, 5, "<1 min")]
    [InlineData("waiting", true, 45, "1 min")]
    [InlineData("busy", true, 90, "2 min")]
    [InlineData("busy", true, 3600, "1 hr")]
    [InlineData("waiting", true, 90060, "1 d 1 hr 1 min")]
    [InlineData("busy", false, 90, null)]
    [InlineData("idle", true, 90, null)]
    [InlineData("unavailable", true, 90, null)]
    [InlineData("busy", true, -1, null)]
    public void DurationUsesOnlyTheCurrentVerifiedWorkingState(string state, bool enabled, int age, string? expected)
        => Assert.Equal(expected, SessionPresentation.Duration(new("test", "codex", "Task", state, Now.AddSeconds(-age)), enabled, Now));

    [Fact]
    public void TokensIncludeDescendantsOnceAndExcludeRemoteChildAndUnmeasuredRows()
    {
        SessionActivity Row(string id, string? stored, string? host = null) => new(id, "codex", id, "busy", Now) { UsageSessionId = stored, RemoteHostId = host };
        UsageEvent Event(string key, string session, long input, long output) => new(key, Now, new(input, 0, output), SessionId: session);
        var first = Event("root", "root", 10, 2);
        var result = SessionPresentation.TokenTotals([Row("main", "root"), Row("child", "child"), Row("remote", "root", "ssh:remote"), Row("empty", "unknown"), Row("zero", "zero")], "codex",
            [first, first, Event("child", "child", 20, 3), Event("grandchild", "grandchild", 30, 4), Event("zero", "zero", 0, 0), Event("other", "unrelated", 100, 0)],
            [new("child", "root", []), new("grandchild", "child", [])], TestContext.Current.CancellationToken);
        Assert.Equal(69, result["main"]); Assert.Equal(0, result["zero"]); Assert.Equal(2, result.Count);
    }
    [Fact]
    public void CyclicMetadataAndOverflowCannotProduceInventedTotals()
    {
        var result = SessionPresentation.TokenTotals([new("cycle", "codex", "Task", "busy", Now) { UsageSessionId = "a" }, new("overflow", "codex", "Task", "busy", Now) { UsageSessionId = "big" }], "codex",
            [new("event", Now, new(1, 0, 0), SessionId: "a"), new("huge", Now, new(long.MaxValue, 0, 1), SessionId: "big")],
            [new("a", "b", []), new("b", "a", [])], TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }
    [Fact]
    public void CancelledTotalsDoNotEnumerateTheHistory()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SessionPresentation.TokenTotals([], "codex", [], [], cancellation.Token));
    }
    [Fact]
    public void RemoteCatalogueExcludesLocalOldFutureMissingAndChildRecordsWithoutMutatingTheSource()
    {
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "coderim-remote-activity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "catalogue.sqlite");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var schema = connection.CreateCommand(); schema.CommandText = """
                    CREATE TABLE local_thread_catalog_hosts(host_id TEXT PRIMARY KEY,host_kind TEXT);
                    CREATE TABLE local_thread_catalog(host_id TEXT,thread_id TEXT,display_title TEXT,cwd TEXT,source_updated_at REAL,missing_candidate INTEGER,source_kind TEXT);
                    INSERT INTO local_thread_catalog_hosts VALUES('remote-one','remote-control'),('wsl-one','wsl'),('local','local');
                    """; schema.ExecuteNonQuery();
                void Insert(string host, int age, int missing = 0, string kind = "cli", string? thread = null, string cwd = "C:\\Projects\\Example")
                {
                    using var insert = connection.CreateCommand(); insert.CommandText = "INSERT INTO local_thread_catalog VALUES($host,$id,'A remote task',$cwd,$date,$missing,$kind)";
                    insert.Parameters.AddWithValue("$host", host); insert.Parameters.AddWithValue("$id", thread ?? Guid.NewGuid().ToString("D"));
                    insert.Parameters.AddWithValue("$cwd", cwd);
                    insert.Parameters.AddWithValue("$date", Now.AddMinutes(-age).ToUnixTimeSeconds()); insert.Parameters.AddWithValue("$missing", missing); insert.Parameters.AddWithValue("$kind", kind); insert.ExecuteNonQuery();
                }
                Insert("remote-one", 5); Insert("wsl-one", 10); Insert("local", 1); Insert("remote-one", 361);
                Insert("remote-one", -5); Insert("remote-one", 1, missing: 1); Insert("remote-one", 1, kind: "subagent"); Insert("remote-one", 1, thread: "invalid");
                Insert("remote-one", 15, cwd: "C:\\Users\\me\\Documents\\Codex\\2026-09-21\\new-chat");
                Insert("remote-one", 20, cwd: "C:\\Users\\me\\.codex");
            }
            var before = File.ReadAllBytes(path); var sessions = CodexRemoteActivity.Read(path, Now, TestContext.Current.CancellationToken);
            Assert.Equal(4, sessions.Count); Assert.All(sessions, x => { Assert.Equal("unavailable", x.State); Assert.NotNull(x.CodexThreadUri); Assert.Null(x.UsageSessionId); Assert.Equal("A remote task", x.Name); });
            Assert.Equal(["Example", "Example", "", "Remote task"], sessions.Select(x => x.Detail));
            Assert.Equal(before, File.ReadAllBytes(path));
            using var corrupt = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); corrupt.Open();
            using var command = corrupt.CreateCommand(); command.CommandText = "ALTER TABLE local_thread_catalog RENAME TO records; CREATE VIEW local_thread_catalog AS SELECT * FROM records;"; command.ExecuteNonQuery();
            Assert.Empty(CodexRemoteActivity.Read(path, Now, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }
}
