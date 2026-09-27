namespace CodeRim.Core.Domain;

/// <summary>Public companion quotas; the desktop restart cache retains the raw reading separately.</summary>
public static class CompanionLimitPresentation
{
    public static ProviderReading ForDisplay(ProviderReading reading, string? rawPlan = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.Id is not ("codex" or "claude")) return reading;
        if (reading.State is not (ReadingState.Ready or ReadingState.Stale))
            return reading with { Windows = [], UpdatedAt = null };
        var windows = reading.Id == "codex"
            ? CodexPlanLimits.VisibleWindows(reading.Windows.Where(window => window.Id != "rate-limit-reset-credits").ToArray(), rawPlan)
            : reading.Windows;
        var namesNeeded = windows.Select(window => AccountLimitPresentation.Name(window, reading.Id)).Distinct(StringComparer.Ordinal).Skip(1).Any();
        return reading with { Windows = windows.Select(window => window with
            { Name = (namesNeeded ? AccountLimitPresentation.Name(window, reading.Id) + " · " : "")
                + AccountLimitPresentation.WindowLabel(window.DurationMinutes) }).ToArray() };
    }
}
