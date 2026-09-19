using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal static class UpdateNotifications
{
    private sealed record Receipt(DateTimeOffset CheckedAt, string? NotifiedVersion);
    internal static string Architecture => RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
    internal static async Task<string?> CheckAsync()
    {
        var path = Path.Combine(CompanionFile.DataDirectory, "update-check.json");
        Receipt? previous = null;
        try { if (File.Exists(path) && new FileInfo(path).Length < 4096) previous = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        var age = previous is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - previous.CheckedAt;
        if (age >= TimeSpan.Zero && age < TimeSpan.FromDays(1)) return null;
        try
        {
            var update = await ReleaseUpdates.CheckAsync(Architecture).ConfigureAwait(false);
            var notify = update.IsNewer && previous?.NotifiedVersion != update.Version.ToString() ? update.Version.ToString() : null;
            var receipt = new Receipt(DateTimeOffset.UtcNow, notify ?? previous?.NotifiedVersion);
            File.WriteAllText(path, JsonSerializer.Serialize(receipt));
            return notify;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or InvalidDataException or OperationCanceledException) { return null; }
    }
}
