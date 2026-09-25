using System.IO;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.ViewModels;

internal sealed partial class DashboardStore
{
    // Read label and ownership together. An external CLI switch must not combine
    // the new login's email with the previous account's cached plan or limits.
    internal (ProviderReading? Reading, string? Label, string? Plan, string? RawPlan, string? OwnerKey) AccountDisplay(string id)
    {
        var reading = Readings.GetValueOrDefault(id);
        if (Synthetic) return (reading, id is "codex" or "claude" ? SavedAccounts.CurrentAccountLabel(id, true) : reading?.Account?.Label, reading?.Plan, reading?.Plan, null);
        var capturedScope = scopes.GetValueOrDefault(id);
        if (id is "codex" or "claude")
        {
            try
            {
                var login = SavedAccounts.Current(id); var identity = login.Identity;
                var owned = identity.Id == capturedScope ? reading : null;
                return (owned, identity.Email, AccountPlanDisplay.Name(id, identity.Plan, login.Profile, identity.Email, identity.Organization) ?? owned?.Plan, identity.Plan, identity.Id);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or FormatException or InvalidOperationException)
            { return (capturedScope is null && reading is { Windows.Count: 0, Plan: null } ? reading : null, null, null, null, null); }
        }
        return (reading, reading?.Account?.Label, reading?.Plan, reading?.Plan, capturedScope);
    }
}
