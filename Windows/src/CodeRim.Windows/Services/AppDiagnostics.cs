using System.Globalization;
using System.IO;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;
internal static class AppDiagnostics
{
    internal static string LogDirectory => Path.Combine(CompanionFile.DataDirectory, "Logs");
    internal static void Record(string provider, string state, int count)
    {
        // This API accepts only catalog IDs, enum values, and numeric counts.
        // Never log exceptions, credentials, paths, prompts, or payloads.
        if (ProviderCatalog.Find(provider) is null
            || !Enum.TryParse<ReadingState>(state, out _) && !Enum.TryParse<DataQuality>(state, out _)) return;
        try
        {
            Directory.CreateDirectory(LogDirectory); CredentialVault.RestrictDirectory(LogDirectory);
            var file = Path.Combine(LogDirectory, "diagnostics.log");
            if (File.Exists(file) && new FileInfo(file).Length > 1_048_576)
                File.Move(file, file + ".previous", overwrite: true);
            File.AppendAllText(file, string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O} {provider} {state} count={count}\n"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
}
