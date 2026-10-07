using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using System.Globalization;

namespace CodeRim.Core.Tests;

public sealed class UsageNormalizerTests
{
    private readonly DateTimeOffset timestamp = DateTimeOffset.Parse(
        "2026-08-27T01:02:03Z",
        CultureInfo.InvariantCulture);

    [Fact]
    public void TotalCountsCachedInputOnlyAsPartOfInput()
    {
        var usage = new TokenUsage(1_200, 800, 300);

        Assert.Equal(1_500, usage.TotalTokens);
    }

    [Fact]
    public void UsesCumulativeIncreaseAndIgnoresRepeatedSnapshot()
    {
        var first = UsageNormalizer.Normalize(Observation(new TokenUsage(100, 60, 20)), UsageNormalizationState.Empty);
        var repeated = UsageNormalizer.Normalize(Observation(new TokenUsage(100, 60, 20)), first.State);
        var increased = UsageNormalizer.Normalize(Observation(new TokenUsage(130, 80, 25)), repeated.State);

        Assert.Equal(new TokenUsage(100, 60, 20), first.Delta);
        Assert.Null(repeated.Delta);
        Assert.Equal(new TokenUsage(30, 20, 5), increased.Delta);
    }

    [Fact]
    public void DoesNotGuessAnAmbiguousInitialBaseline()
    {
        var observation = new TokenObservation(
            timestamp,
            1,
            new TokenUsage(100, 80, 10),
            new TokenUsage(500, 300, 50));

        var result = UsageNormalizer.Normalize(observation, UsageNormalizationState.Empty);

        Assert.Null(result.Delta);
        Assert.Equal(DataQuality.Partial, result.State.Quality);
    }

    [Fact]
    public void CountsAValidatedCounterRestartAsPartial()
    {
        var previous = new UsageNormalizationState(
            new TokenUsage(1_000, 700, 100),
            timestamp.AddMinutes(-1),
            DataQuality.Exact);
        var fresh = new TokenUsage(50, 20, 10);

        var result = UsageNormalizer.Normalize(
            new TokenObservation(timestamp, 2, fresh, fresh),
            previous);

        Assert.Equal(fresh, result.Delta);
        Assert.Equal(DataQuality.Partial, result.State.Quality);
    }


    [Fact]
    public void MissingOptionalCacheWriteDoesNotLoseOrReplayTokens()
    {
        var firstUsage = new TokenUsage(100, 0, 20, 40);
        var first = UsageNormalizer.Normalize(Observation(firstUsage), UsageNormalizationState.Empty);
        var repeated = UsageNormalizer.Normalize(Observation(new(100, 0, 20)), first.State);
        Assert.Null(repeated.Delta);
        var second = UsageNormalizer.Normalize(new(timestamp.AddSeconds(1), 2, new(100, 0, 20), new(200, 0, 40)), first.State);
        var third = UsageNormalizer.Normalize(new(timestamp.AddSeconds(2), 3, new(100, 0, 20), new(300, 0, 60)), second.State);
        Assert.Equal(360, first.Delta!.Value.TotalTokens + second.Delta!.Value.TotalTokens + third.Delta!.Value.TotalTokens);
        Assert.Null(second.Delta.Value.CacheWriteInputTokens);
        Assert.Equal(DataQuality.Exact, third.State.Quality);
    }
    [Fact]
    public void AnInconsistentDeltaBetweenValidCountersIsClampedAndMarkedPartial()
    {
        // 100/20/10 then 110/40/15: each snapshot is valid, the difference (10 input, 20 cached) is not.
        var first = UsageNormalizer.Normalize(Observation(new TokenUsage(100, 20, 10)), UsageNormalizationState.Empty);
        var next = UsageNormalizer.Normalize(Observation(new TokenUsage(110, 40, 15)), first.State);

        Assert.Equal(new TokenUsage(10, 10, 5), next.Delta);
        Assert.True(next.Delta!.Value.IsValid);
        Assert.Equal(DataQuality.Partial, next.State.Quality);
        Assert.Equal("inconsistent token delta clamped", next.Diagnostic);
        Assert.Equal(new TokenUsage(110, 40, 15), next.State.CumulativeHighWaterMark);
    }

    [Fact]
    public void AnInconsistentFirstSnapshotIsStoredAsItsValidPart()
    {
        var result = UsageNormalizer.Normalize(Observation(new TokenUsage(10, 30, 4, 5)), UsageNormalizationState.Empty);

        Assert.Equal(new TokenUsage(10, 10, 4, 0), result.Delta);
        Assert.Equal(DataQuality.Partial, result.State.Quality);
    }

    [Fact]
    public void RepositoryStoresAnInconsistentRowAsValidAndReportsIt()
    {
        var directory = Directory.CreateTempSubdirectory("coderim-repair-").FullName;
        try
        {
            var repository = new UsageRepository(Path.Combine(directory, "usage.sqlite"));
            var stored = repository.Merge("codex", [new("bad", timestamp, new TokenUsage(10, 20, 5), "gpt-5")]);
            Assert.Equal(1, repository.LastWriteRepairedEvents);
            var usage = Assert.Single(stored).Usage;
            Assert.True(usage.IsValid);
            Assert.Equal(15, usage.TotalTokens);
            repository.Merge("codex", [new("good", timestamp, new TokenUsage(10, 2, 5), "gpt-5")]);
            Assert.Equal(0, repository.LastWriteRepairedEvents);
            // The correction stays with the stored history even when no later scan sees the source again.
            var reopened = new UsageRepository(Path.Combine(directory, "usage.sqlite"));
            Assert.True(reopened.HasRepairedEventsSince("codex", timestamp.AddDays(-1)));
            Assert.False(reopened.HasRepairedEventsSince("codex", timestamp.AddDays(1)));
            reopened.Clear("codex", timestamp.AddDays(1));
            Assert.False(reopened.HasRepairedEventsSince("codex", timestamp.AddDays(-1)));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public void RepositoryVersionsItsSchemaAndNeverWritesANewerOne()
    {
        var directory = Directory.CreateTempSubdirectory("coderim-schema-").FullName;
        var path = Path.Combine(directory, "usage.sqlite");
        try
        {
            var repository = new UsageRepository(path);
            repository.Merge("codex", [new("kept", timestamp, new TokenUsage(10, 2, 5), "gpt-5")]);
            Assert.False(repository.IsNewerSchema);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version";
                Assert.Equal(UsageRepository.SchemaVersion, (long)command.ExecuteScalar()!);
                command.CommandText = "PRAGMA user_version = " + (UsageRepository.SchemaVersion + 1);
                command.ExecuteNonQuery();
            }
            var older = new UsageRepository(path);
            Assert.True(older.IsNewerSchema);
            Assert.Single(older.Read("codex"));
            Assert.Throws<InvalidDataException>(() => older.Merge("codex", [new("new", timestamp, new TokenUsage(1, 0, 1), "gpt-5")]));
            Assert.Throws<InvalidDataException>(() => older.Clear("codex", timestamp));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private TokenObservation Observation(TokenUsage usage) =>
        new(timestamp, 1, usage, usage);
}
