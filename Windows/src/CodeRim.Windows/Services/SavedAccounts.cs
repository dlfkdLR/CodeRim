using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeRim.Core.Services;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Windows.Services;

internal sealed record SavedLogin(string Provider, LoginIdentity Identity, string Credential, string? Profile);
internal sealed class SavedAccounts(CredentialVault vault)
{
    private static int switching;
    internal static bool OperationInProgress => Volatile.Read(ref switching) != 0;
    internal IReadOnlyList<SavedLogin> Read(string provider)
    {
        var json = vault.Load("accounts:" + provider);
        if (json is null) return [];
        var saved = JsonSerializer.Deserialize<SavedLogin[]>(json) ?? [];
        if (saved.Length > 12 || saved.Select(x => x.Identity.Id).Distinct(StringComparer.Ordinal).Count() != saved.Length) throw new InvalidDataException("Saved account list is invalid.");
        foreach (var account in saved)
            if (account.Provider != provider || Identity(provider, account.Credential, account.Profile) != account.Identity) throw new InvalidDataException("Saved account identity is invalid.");
        return saved;
    }
    internal static SavedLogin Current(string provider) => ReadLogin(provider, Paths(provider));
    internal static SavedLogin ReadLogin(string provider, (string Credential, string? Profile) paths)
    {
        var credential = GuardedFile.Read(paths.Credential);
        var profile = paths.Profile is { } path ? GuardedFile.Read(path) : null;
        var identity = Identity(provider, credential, profile);
        // Persist only Claude OAuth and account fields, never unrelated configuration.
        if (provider == "claude")
        {
            credential = new JsonObject { ["claudeAiOauth"] = JsonNode.Parse(credential)!["claudeAiOauth"]!.DeepClone() }.ToJsonString();
            profile = new JsonObject { ["oauthAccount"] = JsonNode.Parse(profile!)!["oauthAccount"]!.DeepClone() }.ToJsonString();
        }
        return new(provider, identity, credential, profile);
    }
    internal static string? CurrentAccountLabel(string provider, bool synthetic)
    {
        if (provider is not ("codex" or "claude")) return null;
        if (synthetic) return "preview@example.invalid";
        try { return Current(provider).Identity.Email; }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or FormatException or InvalidOperationException) { return null; }
    }
    internal void SaveCurrent(string provider) => Save(Current(provider));
    internal async Task SaveCurrentAsync(string provider, string executable, Func<Task>? waitForRefresh = null, CancellationToken token = default)
    {
        if (Interlocked.CompareExchange(ref switching, 1, 0) != 0) throw new InvalidOperationException("An account operation is already in progress.");
        try
        {
            if (waitForRefresh is not null) await waitForRefresh().ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            var before = Current(provider);
            CheckPolicy(provider, Paths(provider).Credential);
            if (provider == "codex")
            {
                var configuration = await AppServerClient.ReadAsync(executable, "config/read", token).ConfigureAwait(true);
                LoginIdentity.ValidateCodexPolicy(Get(configuration, "config"), before.Identity.Organization);
            }
            await VerifyAsync(before, executable, token).ConfigureAwait(true);
            var after = Current(provider);
            if (before.Identity.Id != after.Identity.Id) throw new IOException("The CLI login changed during verification.");
            Save(after);
        }
        finally { Interlocked.Exchange(ref switching, 0); }
    }
    internal async Task AddAsync(string provider, string executable, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref switching, 1, 0) != 0) throw new InvalidOperationException("An account operation is already in progress.");
        try
        {
            CheckPolicy(provider, Paths(provider).Credential);
            if (provider == "codex")
            {
                var policy = await AppServerClient.ReadAsync(executable, "config/read", token).ConfigureAwait(true);
                LoginIdentity.ValidateCodexSignInPolicy(Get(policy, "config"));
            }
            var added = await IsolatedAccountSignIn.RunAsync(provider, executable, token: token).ConfigureAwait(true);
            if (provider == "codex")
            {
                var configuration = await AppServerClient.ReadAsync(executable, "config/read", token).ConfigureAwait(true);
                LoginIdentity.ValidateCodexPolicy(Get(configuration, "config"), added.Identity.Organization);
            }
            token.ThrowIfCancellationRequested(); Save(added);
        }
        finally { Interlocked.Exchange(ref switching, 0); }
    }
    internal static async Task<string> VerifyCurrentIdAsync(string provider, string executable, CancellationToken token = default)
    {
        var before = Current(provider);
        await VerifyAsync(before, executable, token).ConfigureAwait(true);
        if (Current(provider).Identity.Id != before.Identity.Id) throw new IOException("The current account changed during verification.");
        return before.Identity.Id;
    }
    private static async Task VerifyAsync(SavedLogin selected, string executable, CancellationToken token)
    {
        if (selected.Provider == "codex")
        {
            var result = await AppServerClient.ReadAsync(executable, "account/read", token).ConfigureAwait(true);
            var active = Get(result, "account");
            if (Text(active, "type") != "chatgpt" || Text(active, "email") != selected.Identity.Email)
                throw new InvalidOperationException("The official Codex CLI could not verify the selected account.");
        }
        else
        {
            using var status = JsonDocument.Parse(await BoundedProcess.RunAsync(executable, ["auth", "status"], cancellationToken: token).ConfigureAwait(true));
            if (Get(status.RootElement, "loggedIn").ValueKind != JsonValueKind.True || Text(status.RootElement, "authMethod") != "claude.ai"
                || Text(status.RootElement, "email") != selected.Identity.Email || Text(status.RootElement, "orgId") != selected.Identity.Organization)
                throw new InvalidOperationException("The official Claude CLI could not verify the selected account.");
        }
    }
    private void Save(SavedLogin account)
    {
        var entries = Read(account.Provider).Where(x => x.Identity.Id != account.Identity.Id).Append(account).ToArray();
        if (entries.Length > 12) throw new InvalidOperationException("You can save up to 12 accounts per provider.");
        var json = JsonSerializer.Serialize(entries);
        if (json.Length > 500000) throw new InvalidOperationException("The saved account vault is full.");
        vault.Save("accounts:" + account.Provider, json);
    }
    internal void Remove(string provider, string id)
    {
        if (Interlocked.CompareExchange(ref switching, 1, 0) != 0) throw new InvalidOperationException("Wait for the current account operation to finish.");
        try { vault.Save("accounts:" + provider, JsonSerializer.Serialize(Read(provider).Where(x => x.Identity.Id != id).ToArray())); }
        finally { Interlocked.Exchange(ref switching, 0); }
    }
    internal async Task SwitchAsync(SavedLogin selected, string executable, Func<Task>? waitForRefresh = null, CancellationToken token = default)
    {
        if (Interlocked.CompareExchange(ref switching, 1, 0) != 0) throw new InvalidOperationException("An account operation is already in progress.");
        try
        {
            if (waitForRefresh is not null) await waitForRefresh().ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            var paths = Paths(selected.Provider);
            if (selected.Provider == "codex")
            {
                var configuration = await AppServerClient.ReadAsync(executable, "config/read", token).ConfigureAwait(true);
                LoginIdentity.ValidateCodexPolicy(Get(configuration, "config"), selected.Identity.Organization);
            }
            CheckPolicy(selected.Provider, paths.Credential);
            var processes = Process.GetProcessesByName(selected.Provider);
            try { if (processes.Any(x => !x.HasExited)) throw new InvalidOperationException("Close the provider's CLI and editor sessions before switching accounts."); }
            finally { foreach (var process in processes) process.Dispose(); }
            var before = GuardedFile.Read(paths.Credential); var profileBefore = paths.Profile is { } file ? GuardedFile.Read(file) : null;
            SaveCurrent(selected.Provider);
            var after = selected.Provider == "claude" ? Merge(before, selected.Credential, "claudeAiOauth") : selected.Credential;
            var profileAfter = selected.Provider == "claude" ? Merge(profileBefore!, selected.Profile!, "oauthAccount") : null;
            var credentialWritten = false; var profileWritten = false;
            try
            {
                GuardedFile.Replace(paths.Credential, before, after); credentialWritten = true;
                if (paths.Profile is { } profilePath) { GuardedFile.Replace(profilePath, profileBefore!, profileAfter!); profileWritten = true; }
                await VerifyAsync(selected, executable, token).ConfigureAwait(true);
                var current = Current(selected.Provider);
                if (current.Identity.Id != selected.Identity.Id) throw new IOException("The provider login changed during verification.");
                Save(current);
            }
            catch
            {
                // Restore only our exact writes; a newer external login is never overwritten.
                var credentialStillOurs = !credentialWritten || GuardedFile.Read(paths.Credential) == after;
                var profileStillOurs = paths.Profile is null || GuardedFile.Read(paths.Profile) == (profileWritten ? profileAfter : profileBefore);
                if (credentialStillOurs && profileStillOurs)
                {
                    if (profileWritten && paths.Profile is { } profilePath) GuardedFile.Replace(profilePath, profileAfter!, profileBefore!);
                    if (credentialWritten) GuardedFile.Replace(paths.Credential, after, before);
                }
                throw;
            }
        }
        finally { Interlocked.Exchange(ref switching, 0); }
    }
    private static LoginIdentity Identity(string provider, string credential, string? profile) => provider == "codex" ? LoginIdentity.Codex(credential) : LoginIdentity.Claude(credential, profile ?? throw new InvalidDataException("Claude account profile is missing."));
    private static string Merge(string current, string selected, string key)
    {
        var root = JsonNode.Parse(current)!.AsObject(); root[key] = JsonNode.Parse(selected)![key]!.DeepClone(); return root.ToJsonString();
    }
    internal static (string Credential, string? Profile) Paths(string provider)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (provider == "codex") return (Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex"), "auth.json"), null);
        if (provider != "claude") throw new ArgumentException("Unknown account provider.", nameof(provider));
        var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return (Path.Combine(config ?? Path.Combine(home, ".claude"), ".credentials.json"), Path.Combine(config ?? home, ".claude.json"));
    }
    private static void CheckPolicy(string provider, string credential)
    {
        var variables = provider == "codex" ? new[] { "OPENAI_API_KEY", "CODEX_API_KEY" } : new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY" };
        if (variables.Any(x => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(x)))) throw new InvalidOperationException("This provider uses external authentication. Switch through its CLI.");
        if (provider == "codex")
        {
            var config = Path.Combine(Path.GetDirectoryName(credential)!, "config.toml");
            if (File.Exists(config) && File.ReadAllLines(config).Any(x => x.TrimStart().StartsWith("cli_auth_credentials_store", StringComparison.Ordinal) && !x.Contains("\"file\"", StringComparison.Ordinal) && !x.Contains("'file'", StringComparison.Ordinal)))
                throw new InvalidOperationException("This Codex login is managed by configuration. Switch through Codex.");
        }
    }
}
