using CodeRim.Core.Domain;
namespace CodeRim.Windows.Services;
internal static class ProviderDisplayPolicy
{
    internal static ProviderReading? Apply(ProviderReading? reading, AppSettings settings)
    {
        if (reading is null || reading.Id != "codex") return reading;
        if (!settings.AccountLimitsEnabled) return reading with { State = ReadingState.Disabled, Windows = [],
            Message = "Account limits are turned off in Settings." };
        return reading with { Windows = reading.Windows.Where(window =>
            window.Id == "rate-limit-reset-credits" ? settings.ResetCreditsEnabled :
            settings.AdditionalLimitsEnabled || window.Id.StartsWith("codex.", StringComparison.Ordinal)
                || window.Id is "session" or "weekly").ToArray() };
    }
}
