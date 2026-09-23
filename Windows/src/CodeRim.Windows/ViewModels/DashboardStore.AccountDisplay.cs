using System.IO;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.ViewModels;

internal sealed partial class DashboardStore
{
    // Read label and ownership together. An external CLI switch must not combine
    // the new login's email with the previous account's cached plan or limits.
    internal (ProviderReading? Reading, string? Label) AccountDisplay(string id)
    {
        var reading = Readings.GetValueOrDefault(id);
        if (Synthetic) return (reading, SavedAccounts.CurrentAccountLabel(id, true));
        var capturedScope = scopes.GetValueOrDefault(id);
        if (id is "codex" or "claude")
        {
            try
            {
                var identity = SavedAccounts.Current(id).Identity;
                return (identity.Id == capturedScope ? reading : null, identity.Email);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or FormatException or InvalidOperationException)
            { return (capturedScope is null && reading is { Windows.Count: 0, Plan: null } ? reading : null, null); }
        }
        return (reading, null);
    }
}
