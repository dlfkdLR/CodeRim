using CodeRim.Core.Domain;
namespace CodeRim.Windows.Services;
internal static class ProviderDisplayPolicy
{
    internal const string ResetCreditsId = "rate-limit-reset-credits";
    internal static ProviderReading? ForNotch(ProviderReading? reading, AppSettings settings)
    {
        var displayed = Apply(reading, settings);
        // The reference maps only quota windows into its notch snapshot.
        // Reset credits have their own read-only row in Usage > Codex Limits.
        return displayed?.Id == "codex" ? displayed with { Windows = displayed.Windows.Where(x => x.Id != ResetCreditsId).ToArray() } : displayed;
    }
    internal static ProviderReading? Apply(ProviderReading? reading, AppSettings settings)
    {
        if (reading is null || reading.Id != "codex") return reading;
        if (!settings.AccountLimitsEnabled) return reading with { State = ReadingState.Disabled, Windows = [],
            Message = "Account limits are turned off in Settings." };
        return reading with { Windows = reading.Windows.Where(window =>
            window.Id == ResetCreditsId ? settings.ResetCreditsEnabled :
            settings.AdditionalLimitsEnabled || window.Id.StartsWith("codex.", StringComparison.Ordinal)
                || window.Id is "session" or "weekly").ToArray() };
    }
}
