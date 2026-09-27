using System.Globalization;

namespace CodeRim.Core.Domain;

public enum LimitPaceState { Ahead, OnPace, Reserve }
public sealed record LimitPace(LimitPaceState State, double DifferencePercent, DateTimeOffset? ProjectedExhaustion)
{
    public string Summary => State == LimitPaceState.OnPace ? "On even pace"
        : Math.Round(DifferencePercent, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.CurrentCulture)
            + (State == LimitPaceState.Ahead ? "% above even pace" : "% below even pace");
}

/// <summary>Account Limits card presentation. Notch plan filtering is deliberately separate.</summary>
public static class AccountLimitPresentation
{
    public static string LimitId(LimitWindow window) => window.Id is "session" or "weekly" ? "codex"
        : window.Id.EndsWith(".primary", StringComparison.Ordinal) ? window.Id[..^8]
        : window.Id.EndsWith(".secondary", StringComparison.Ordinal) ? window.Id[..^10] : window.Id;

    public static string Name(LimitWindow window, string provider) => window.AccountLimitName
        ?? (provider == "claude" ? "Claude" : FriendlyName(LimitId(window)));

    public static string FriendlyName(string id) => id.ToLowerInvariant() switch
    {
        "codex" => "Codex",
        "codex_bengalfox" => "GPT-5.3-Codex-Spark",
        _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(id.Replace('_', ' '))
    };

    public static string WindowLabel(int minutes) => minutes switch
    {
        >= 270 and <= 330 => "5 hours",
        >= 9000 and <= 11000 => "Weekly",
        >= 1440 => (minutes / 1440).ToString(CultureInfo.CurrentCulture) + (minutes / 1440 == 1 ? " day" : " days"),
        _ => minutes.ToString(CultureInfo.CurrentCulture) + (minutes == 1 ? " minute" : " minutes")
    };

    public static IReadOnlyList<LimitWindow> VisibleWindows(IReadOnlyList<LimitWindow> windows, bool includesAdditional)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return windows.Where(window => includesAdditional || IsPrimary(window))
            .OrderBy(window => IsPrimary(window) && window.DurationMinutes is >= 9000 and <= 11000 ? 0 : 1)
            .ThenBy(window => window.DurationMinutes)
            .ThenBy(window => Name(window, "codex"), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.Id, StringComparer.Ordinal).ToArray();
    }

    public static bool IsPrimary(LimitWindow window) => string.Equals(LimitId(window), "codex", StringComparison.OrdinalIgnoreCase);

    public static LimitPace? Pace(LimitWindow window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.DurationMinutes is <= 0 or > 527040 || window.ResetsAt is not { } reset || reset <= now
            || window.UsedPercent is not { } used || !double.IsFinite(used)) return null;
        var duration = window.DurationMinutes * 60d;
        var remaining = (reset - now).TotalSeconds;
        var elapsed = duration - remaining;
        if (elapsed <= 0) return null;
        var expected = Math.Clamp(elapsed / duration * 100, 0, 100);
        if (expected < 3) return null;
        var observed = Math.Clamp(used, 0, 100); var difference = observed - expected;
        var state = Math.Abs(difference) < 1 ? LimitPaceState.OnPace : difference > 0 ? LimitPaceState.Ahead : LimitPaceState.Reserve;
        DateTimeOffset? exhaustion = null;
        if (observed > 0)
        {
            var seconds = (100 - observed) / (observed / elapsed);
            // Compare before adding: a tiny usage rate must not overflow DateTimeOffset.
            if (double.IsFinite(seconds) && seconds < remaining) exhaustion = now.ToUniversalTime().AddSeconds(seconds);
        }
        return new(state, Math.Abs(difference), exhaustion);
    }

    public static string Freshness(DateTimeOffset fetchedAt, DateTimeOffset now, bool stale = false)
    {
        var age = Math.Max(0, (now - fetchedAt).TotalSeconds);
        var elapsed = age switch
        {
            < 5 => "just now",
            < 60 => (int)age + " sec ago",
            < 3600 => (int)(age / 60) + " min ago",
            < 86400 => (int)(age / 3600) + " hr ago",
            _ => (int)(age / 86400) + ((int)(age / 86400) == 1 ? " day ago" : " days ago")
        };
        return (stale ? "Last known · updated " : "Updated ") + elapsed;
    }
}
