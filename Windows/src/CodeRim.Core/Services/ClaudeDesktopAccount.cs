using System.IO;
using System.Text.Json;

namespace CodeRim.Core.Services;

/// <summary>
/// Which account Claude Desktop is signed in to, as far as CodeRim can tell. Desktop keeps
/// its own encrypted sign-in in config.json, separate from the Claude Code CLI's, and rewrites
/// it while running, so CodeRim never edits it. It only reads the account UUID Desktop records
/// beside it, to say after a CLI switch when Desktop is still on another account.
/// </summary>
public static class ClaudeDesktopAccount
{
    public static string ConfigurationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "config.json");

    public static string? AccountId(string? path = null)
    {
        try
        {
            var file = new FileInfo(path ?? ConfigurationPath);
            if (!file.Exists || file.Length > 4 * 1024 * 1024) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("lastKnownAccountUuid", out var id) && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } value ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>The <c>oauthAccount.accountUuid</c> of a saved Claude Code profile.</summary>
    public static string? ProfileAccountId(string? profile)
    {
        if (string.IsNullOrEmpty(profile)) return null;
        try
        {
            using var document = JsonDocument.Parse(profile);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("oauthAccount", out var account) && account.ValueKind == JsonValueKind.Object
                && account.TryGetProperty("accountUuid", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>True only when Desktop is known to be signed in to a different account.</summary>
    public static bool Differs(string? cliAccountId, string? path = null) =>
        !string.IsNullOrEmpty(cliAccountId) && AccountId(path) is { } desktop
        && !string.Equals(desktop, cliAccountId, StringComparison.OrdinalIgnoreCase);
}
