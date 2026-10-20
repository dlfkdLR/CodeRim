namespace CodeRim.Core.Domain;

/// <summary>Quota visibility from the live Codex plan, independent of its display label.</summary>
public static class CodexPlanLimits
{
    public static IReadOnlyList<LimitWindow> VisibleWindows(IReadOnlyList<LimitWindow> windows, string? plan)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (!string.Equals(plan?.Trim(), "pro", StringComparison.OrdinalIgnoreCase)) return windows;
        var kept = windows.Where(window => window.DurationMinutes is < 270 or > 330).ToArray();
        // Keep the reported data if this is the account's only quota, as on Mac.
        return kept.Length == 0 ? windows : kept;
    }
}
