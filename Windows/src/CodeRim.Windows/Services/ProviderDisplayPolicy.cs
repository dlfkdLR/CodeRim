using CodeRim.Core.Domain;
namespace CodeRim.Windows.Services;
internal static class ProviderDisplayPolicy
{
    internal const string ResetCreditsId = "rate-limit-reset-credits";
    internal static ProviderReading? ForSettings(ProviderReading? reading)
    {
        // Mac removes these providers' snapshot on a failed fetch. Keep the
        // stored failure for notch presence/diagnostics, but present that same
        // absent snapshot in Settings without discarding independent Account.
        return reading is not null && ProviderAvailability.HidesWhenAbsent(reading.Id)
            && reading.State is ReadingState.NeedsAuth or ReadingState.Unsupported
                or ReadingState.Unavailable or ReadingState.Error or ReadingState.Disabled
            ? null : reading;
    }
    internal static ProviderReading? ForNotch(ProviderReading? reading, AppSettings settings, string? rawPlan = null)
    {
        var displayed = Apply(reading, settings);
        // The reference maps only quota windows into its notch snapshot.
        // Reset credits have their own read-only row in Usage > Codex Limits.
        return displayed?.Id == "codex" ? displayed with { Windows = CodexPlanLimits.VisibleWindows(
            displayed.Windows.Where(x => x.Id != ResetCreditsId).ToArray(), rawPlan) } : displayed;
    }
    internal static ProviderReading? Apply(ProviderReading? reading, AppSettings settings)
    {
        if (reading is null || reading.Id != "codex") return reading;
        if (!settings.AccountLimitsEnabled) return reading with { State = ReadingState.Disabled, Windows = [],
            Message = "Account limits are turned off in Settings." };
        return reading with { Windows = reading.Windows.Where(window =>
            window.Id == ResetCreditsId ? settings.ResetCreditsEnabled :
            settings.AdditionalLimitsEnabled || AccountLimitPresentation.IsPrimary(window)).ToArray() };
    }
}
