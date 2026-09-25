using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

/// <summary>Local transcript state is independent from account quota state.</summary>
public static class LocalTokenPresentation
{
    public const string Scope = "This PC · Since local midnight";
    public const string ScopeHelp = "Tokens recorded on this PC since midnight in its current time zone, across accounts and sessions, including background agents. Cached input is included in the total. Account quota percentages use their own reset periods.";

    // An absent snapshot has not finished its first read. An empty completed
    // read is Unavailable; neither state is a measured zero.
    public static string Text(UsageSnapshot? snapshot, TokenNumberStyle style)
    {
        if (snapshot is null) return "Loading…";
        if (snapshot.Quality is DataQuality.Unavailable or DataQuality.Error) return "Unavailable";
        var value = TokenFormatter.Format(snapshot.Today.TotalTokens, style) + " tokens";
        return snapshot.Quality == DataQuality.Stale ? value + " (stale)" : value;
    }

    public static UsageSnapshot AfterFailure(UsageSnapshot? snapshot)
    {
        var retained = snapshot ?? UsageSnapshot.Empty;
        return retained with { Quality = retained.UpdatedAt is null ? DataQuality.Error : DataQuality.Stale };
    }

    // Aggregate can report Partial for an incomplete/empty source inventory.
    // Without any observed event timestamp, its zero is not a measurement.
    public static UsageSnapshot CompletedRead(UsageSnapshot snapshot) => snapshot.UpdatedAt is null
        ? snapshot with { Quality = DataQuality.Unavailable } : snapshot;
}
