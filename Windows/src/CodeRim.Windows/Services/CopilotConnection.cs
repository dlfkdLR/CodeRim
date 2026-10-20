using System.IO;
using CodeRim.Core.Services;
namespace CodeRim.Windows.Services;

internal static class CopilotConnection
{
    internal static string Directory => GitHubAuthentication.ConfigurationDirectory(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable, windows: true);
    internal static string? ScopeMarker() => GitHubAuthentication.ReadHosts(Directory);
    internal static async Task<string?> ReadCliAsync(CancellationToken token)
    {
        var executable = ProviderConnections.ResolveExecutable("gh.exe");
        if (executable is null)
        {
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe");
            if (File.Exists(installed)) executable = installed;
        }
        if (executable is null) return null;
        // Clear ambient auth/debug/extension configuration; gh auth token is a
        // fixed read-only command against this user's selected github.com profile.
        var environment = Environment.GetEnvironmentVariables().Keys.Cast<string>().ToDictionary(key => key, _ => (string?)null, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "SystemRoot", "WINDIR", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "LANG" })
            environment[key] = Environment.GetEnvironmentVariable(key);
        environment["GH_CONFIG_DIR"] = Directory; environment["GH_PROMPT_DISABLED"] = "1";
        environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        try
        {
            return GitHubAuthentication.Token(await BoundedProcess.RunAsync(executable, ["auth", "token", "--hostname", "github.com"],
                timeout: TimeSpan.FromSeconds(10), maximumBytes: 65536, environment: environment, cancellationToken: token).ConfigureAwait(false));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); return null; }
    }
}
