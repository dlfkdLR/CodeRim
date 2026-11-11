using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class UsagePeriodContractTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var resource = typeof(UsagePeriodContractTests).Assembly.GetManifestResourceStream("usage-periods-v1.json")!;
        using var document = JsonDocument.Parse(resource);
        Assert.NotEmpty(document.RootElement.EnumerateArray());
        foreach (var item in document.RootElement.EnumerateArray())
            yield return [item.GetProperty("name").GetString()!, item.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PreservesPeriodsAndPersistedRepairEvidence(string name, string json)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(json, JsonSerializerOptions.Web)!;
        Assert.Equal(name, fixture.Name);
        var root = Directory.CreateTempSubdirectory("coderim-period-contract-").FullName;
        try
        {
            var path = Path.Combine(root, "usage.sqlite");
            var events = fixture.Events.Select((row, index) => new UsageEvent(index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.FromUnixTimeSeconds(row.At), new TokenUsage(row.Tokens, 0, 0, 0)) { Repaired = row.Repaired }).ToArray();
            new UsageRepository(path).Merge("codex", events);
            var reopened = new UsageRepository(path);
            var saved = reopened.Read("codex");
            var snapshot = UsageAggregation.Aggregate(saved, DateTimeOffset.FromUnixTimeSeconds(fixture.Now),
                Enum.Parse<WeekStart>(fixture.WeekStart, ignoreCase: true), false,
                TimeZoneInfo.FindSystemTimeZoneById(fixture.TimeZone));
            long[] totals = [snapshot.Today.TotalTokens, snapshot.Week.TotalTokens,
                snapshot.Month.TotalTokens, snapshot.AllTime.TotalTokens];
            Assert.Equal(fixture.Expected.Totals, totals);
            Assert.Equal(fixture.Expected.UpdatedAt, snapshot.UpdatedAt?.ToUnixTimeSeconds());
            Assert.Equal(Enum.Parse<DataQuality>(fixture.Expected.Quality, ignoreCase: true), snapshot.Quality);
            Assert.Equal(fixture.Expected.HasRepairEvidence, reopened.HasRepairedEventsSince("codex", DateTimeOffset.UnixEpoch));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed record Fixture(string Name, long Now, string TimeZone, string WeekStart, Event[] Events, Expected Expected);
    private sealed record Event(long At, long Tokens, bool Repaired);
    private sealed record Expected(long[] Totals, string Quality, long? UpdatedAt, bool HasRepairEvidence);
}
