using System.IO;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.ViewModels;

internal sealed partial class DashboardStore
{
    // Read label and ownership together. An external CLI switch must not combine
    // the new login's email with the previous account's cached plan or limits.
    internal (ProviderReading? Reading, string? Label, string? Plan) AccountDisplay(string id)
    {
        var reading = Readings.GetValueOrDefault(id);
        if (Synthetic) return (reading, SavedAccounts.CurrentAccountLabel(id, true), reading?.Plan);
        var capturedScope = scopes.GetValueOrDefault(id);
        if (id is "codex" or "claude")
        {
            try
            {
                var identity = SavedAccounts.Current(id).Identity;
                var owned = identity.Id == capturedScope ? reading : null;
                return (owned, identity.Email, PlanName(id, identity.Plan) ?? owned?.Plan);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or FormatException or InvalidOperationException)
            { return (capturedScope is null && reading is { Windows.Count: 0, Plan: null } ? reading : null, null, null); }
        }
        return (reading, null, reading?.Plan);
    }
    private static string? PlanName(string id, string? plan)
    {
        plan = plan?.Trim();
        if (plan is not { Length: > 0 and <= 80 } || plan.Any(char.IsControl)) return null;
        return (id, plan.ToLowerInvariant()) switch {
            ("codex", "prolite") => "Pro 5x", ("codex", "pro") => "Pro 20x",
            _ => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(plan.Replace('_', ' ')) };
    }
}
