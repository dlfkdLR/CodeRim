using System.IO;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Windows.Services;

/// <summary>A failed read that had already renewed the login. The renewed copy is the only one that still works, so it travels with the error.</summary>
internal sealed class SavedAccountReadException(Exception inner, string rotatedCredential)
    : IOException("The saved account's limits could not be read.", inner)
{
    internal string RotatedCredential { get; } = rotatedCredential;
}

/// <summary>
/// Read saved-account limits in a disposable private Codex home; never log out or change the active CLI login.
/// </summary>
internal static class SavedAccountLimitReader
{
    internal delegate Task<JsonElement> Rpc(string executable, string method, IReadOnlyDictionary<string, string?> environment, CancellationToken token);

    /// <summary>Limits for <paramref name="saved"/>, plus the renewed credential when the probe rotated it.</summary>
    internal static async Task<(IReadOnlyList<LimitWindow> Windows, string? RotatedCredential)> ReadAsync(
        SavedLogin saved, string executable, Rpc? rpc = null, CancellationToken token = default)
    {
        if (saved.Provider != "codex") throw new ArgumentException("Only Codex accounts can be read this way.", nameof(saved));
        rpc ??= (exe, method, env, ct) => AppServerClient.ReadAsync(exe, method, env, inheritEnvironment: false, ct);
        var parent = Path.Combine(CompanionFile.DataDirectory, "vault");
        Directory.CreateDirectory(parent);
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked login directories are not supported.");
        var root = Path.Combine(parent, "limits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); CredentialVault.RestrictDirectory(root);
        var authFile = Path.Combine(root, "auth.json");
        IReadOnlyList<LimitWindow>? windows = null; Exception? failure = null; string? rotated = null;
        try
        {
            GuardedFile.WritePrivate(Path.Combine(root, "config.toml"), "cli_auth_credentials_store = \"file\"\n");
            GuardedFile.WritePrivate(authFile, saved.Credential);
            try
            {
                var result = await rpc(executable, "account/rateLimits/read", IsolatedAccountSignIn.EnvironmentFor("codex", root), token).ConfigureAwait(false);
                windows = Codex(result);
            }
            catch (Exception error) { failure = error; }
            // The probe may have renewed the login whether or not the read worked.
            rotated = RenewedCredential(saved, authFile);
        }
        finally { await RemoveAsync(root).ConfigureAwait(false); }
        if (failure is not null)
        {
            if (rotated is not null) throw new SavedAccountReadException(failure, rotated);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return (windows!, rotated);
    }

    /// <summary>The credential now in the private home when it is a valid login for the same account and differs from <paramref name="saved"/>.</summary>
    private static string? RenewedCredential(SavedLogin saved, string authFile)
    {
        try
        {
            var after = GuardedFile.Read(authFile);
            if (after == saved.Credential) return null;
            var parsed = SavedAccounts.ParseCredential("codex", after);
            return parsed.Identity.Id == saved.Identity.Id ? parsed.Credential : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        { return null; }
    }

    private static async Task RemoveAsync(string root)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(root, recursive: true); return; }
            catch (DirectoryNotFoundException) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 4) throw new IOException("The temporary limits read could not be cleaned up.", error);
                await Task.Delay(100 * (attempt + 1), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
