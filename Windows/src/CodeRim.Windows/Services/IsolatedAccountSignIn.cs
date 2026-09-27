using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using CodeRim.Core.Services;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Windows.Services;

/// <summary>Adding a saved account must not log the user's existing CLI out.</summary>
internal static class IsolatedAccountSignIn
{
    internal delegate Task<ProcessResult> Command(string executable, string[] arguments, IReadOnlyDictionary<string, string?> environment, CancellationToken token);
    internal delegate Task<JsonElement> Rpc(string executable, string method, IReadOnlyDictionary<string, string?> environment, CancellationToken token);

    internal static async Task<SavedLogin> RunAsync(string provider, string executable,
        Command? command = null, Rpc? rpc = null, CancellationToken token = default)
    {
        if (provider is not ("codex" or "claude")) throw new ArgumentException("Unsupported account provider.", nameof(provider));
        command ??= (exe, args, env, ct) => BoundedProcess.RunIsolatedResultAsync(exe, args, env, TimeSpan.FromMinutes(4), 131072, ct);
        rpc ??= (exe, method, env, ct) => AppServerClient.ReadAsync(exe, method, env, inheritEnvironment: false, ct);
        var parent = Path.Combine(CompanionFile.DataDirectory, "vault");
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked login directories are not supported.");
        var root = Path.Combine(parent, "sign-in-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); CredentialVault.RestrictDirectory(root);
        SavedLogin? verified = null; ExceptionDispatchInfo? failure = null;
        try
        {
            var environment = EnvironmentFor(provider, root);
            if (provider == "codex")
            {
                GuardedFile.WritePrivate(Path.Combine(root, "config.toml"), "cli_auth_credentials_store = \"file\"\n");
                var probe = await rpc(executable, "account/read", environment, token).ConfigureAwait(false);
                // An old CLI that ignores the isolated home must never launch login.
                if (Get(probe, "account").ValueKind != JsonValueKind.Null) throw new InvalidOperationException("The temporary Codex login is not isolated.");
            }
            else
            {
                var probe = await command(executable, ["auth", "status"], environment, token).ConfigureAwait(false);
                using var status = JsonDocument.Parse(probe.Output);
                if (probe.ExitCode is not (0 or 1) || Get(status.RootElement, "loggedIn").ValueKind != JsonValueKind.False)
                    throw new InvalidOperationException("The temporary Claude login is not isolated.");
            }

            var login = await command(executable, provider == "codex" ? ["-c", "cli_auth_credentials_store=\"file\"", "login"] : ["auth", "login", "--claudeai"], environment, token).ConfigureAwait(false);
            if (login.ExitCode != 0) throw new IOException("The official provider sign-in did not complete.");
            token.ThrowIfCancellationRequested();
            var paths = provider == "codex" ? (Path.Combine(root, "auth.json"), (string?)null)
                : (Path.Combine(root, ".credentials.json"), Path.Combine(root, ".claude.json"));
            var account = SavedAccounts.ReadLogin(provider, paths);
            if (provider == "codex")
            {
                var result = await rpc(executable, "account/read", environment, token).ConfigureAwait(false);
                var active = Get(result, "account");
                if (Text(active, "type") != "chatgpt" || Text(active, "email") != account.Identity.Email)
                    throw new InvalidDataException("The new Codex login could not be verified.");
            }
            else
            {
                var result = await command(executable, ["auth", "status"], environment, token).ConfigureAwait(false);
                using var status = JsonDocument.Parse(result.Output);
                if (result.ExitCode != 0 || Get(status.RootElement, "loggedIn").ValueKind != JsonValueKind.True
                    || Text(status.RootElement, "authMethod") != "claude.ai" || Text(status.RootElement, "email") != account.Identity.Email
                    || Text(status.RootElement, "orgId") != account.Identity.Organization)
                    throw new InvalidDataException("The new Claude login could not be verified.");
            }
            token.ThrowIfCancellationRequested();
            var latest = SavedAccounts.ReadLogin(provider, paths);
            if (latest.Identity.Id != account.Identity.Id) throw new IOException("The login changed during verification.");
            verified = latest;
        }
        catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        // Never invoke logout/revoke: only this disposable child directory is removed.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(root, recursive: true); break; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 4) throw new IOException("The temporary sign-in could not be cleaned up. Retry after closing the provider CLI.", error);
                await Task.Delay(100 * (attempt + 1), CancellationToken.None).ConfigureAwait(false);
            }
        }
        failure?.Throw();
        return verified!;
    }

    internal static IReadOnlyDictionary<string, string?> EnvironmentFor(string provider, string root)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "SystemRoot", "WINDIR", "COMSPEC", "PATH", "PATHEXT", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "LANG", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "SSL_CERT_FILE" })
            if (Environment.GetEnvironmentVariable(key) is { } value) values[key] = value;
        if (provider == "codex") values["CODEX_HOME"] = root;
        else { values["CLAUDE_CONFIG_DIR"] = root; values["CLAUDE_SECURESTORAGE_CONFIG_DIR"] = root; }
        return values;
    }
}
