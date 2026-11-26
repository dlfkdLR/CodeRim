using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal sealed partial class ProviderConnections
{
    internal string? OwnedSetting(string id, string key) =>
        OwnedProviderAccess.IsConnectionSetting(key) ? vault.Load("setting:" + id + ":" + key)?.Trim() : null;

    private string? OwnedCredential(string id)
    {
        var keys = OwnedProviderAccess.CredentialKeys(id);
        if (keys.Count == 0) return null;
        if (id == "moonshot")
            return MoonshotAuthentication.Region(OwnedSetting(id, "MOONSHOT_REGION")) is { } moonshotRegion
                ? MoonshotAuthentication.Credential(moonshotRegion, vault.Load, Environment.GetEnvironmentVariable) : null;
        if (id == "deepseek")
            return DeepSeekAuthentication.Resolve("api", vault.Load, Environment.GetEnvironmentVariable)?.Token;
        if (id == "minimax")
        {
            var region = MiniMaxAuthentication.Region(OwnedSetting(id, "MINIMAX_REGION"));
            if (region is null) return null;
            return vault.Load("provider:minimax:" + region)
                ?? (vault.Load("setting:minimax:LEGACY_REGION") == region ? vault.Load("provider:minimax") : null)
                ?? (MiniMaxAuthentication.Region(Environment.GetEnvironmentVariable("MINIMAX_REGION")) == region
                    ? Environment.GetEnvironmentVariable("MINIMAX_CODING_API_KEY") ?? Environment.GetEnvironmentVariable("MINIMAX_API_KEY") : null);
        }
        if (id is "copilot" or "gemini") return vault.Load("provider:" + id);
        foreach (var key in keys)
            if (vault.Load("setting:" + id + ":" + key) is { Length: > 0 } custom) return custom;
        if (vault.Load("provider:" + id) is { Length: > 0 } manual
            && (id is not ("factory" or "groq") || OwnedProviderAccess.IsPlainApiKey(manual))) return manual;
        foreach (var key in keys)
            if (OwnedProviderAccess.IsExplicitApiKey(key) && Environment.GetEnvironmentVariable(key) is { Length: > 0 } provided) return provided;
        return null;
    }

    internal SavedLogin? SelectedAccount(string id)
    {
        try { return new SavedAccounts(vault).SelectedForCodeRim(id); }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or JsonException
            or UnauthorizedAccessException or FormatException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        { return null; }
    }

    private string? OwnedScope(string id)
    {
        if (id is "codex" or "claude") return SelectedAccount(id)?.Identity.Id;
        if (OwnedProviderAccess.HasLocalEndpoint(id)) return null;
        var key = OwnedCredential(id);
        if (key is null) return null;
        var names = NativeProviders.Settings(id).Select(x => x.Key)
            .Concat(ScriptProviders.Catalog.TryGetValue(id, out var script) ? script.Settings.Select(x => x.Key) : [])
            .Concat(NativeProviders.ScopeAliases(id)).Concat(ProviderCatalog.Find(id)?.EnvironmentKeys ?? [])
            .Where(OwnedProviderAccess.IsConnectionSetting).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return ScopeHash(JsonSerializer.Serialize(new { source = "coderim-owned-v1", id, key,
            settings = names.Select(n => OwnedSetting(id, n)).ToArray() }));
    }

    private async Task<ProviderReading> FetchOwnedAsync(string id, AppSettings settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (id == "claude") return await ReadClaudeSnapshotAsync(() => SelectedAccount(id)?.Identity.Id, token).ConfigureAwait(false);
        if (id == "codex")
        {
            if (!settings.AccountLimitsEnabled) return new(id, ReadingState.Disabled, [], Message: "Account limits are turned off in Settings.");
            if (SelectedAccount(id) is not { } selected)
                return new(id, ReadingState.NeedsAuth, [], Message: "Choose or add a Codex account in CodeRim Settings.");
            var executable = settings.CodexExecutable ?? ResolveCodex();
            if (executable is null) return new(id, ReadingState.NeedsAuth, [], Message: "Install the official Codex CLI to refresh this account.");
            var accounts = new SavedAccounts(vault);
            try
            {
                var read = await SavedAccountLimitReader.ReadAsync(selected, executable, token: token).ConfigureAwait(false);
                if (read.RotatedCredential is { } rotated) accounts.StoreRotated(selected, rotated);
                return new(id, read.Windows.Count > 0 ? ReadingState.Ready : ReadingState.Unavailable, read.Windows, DateTimeOffset.Now,
                    Plan: selected.Identity.Plan, Account: new(selected.Identity.Email, "Codex"));
            }
            catch (SavedAccountReadException error)
            {
                accounts.StoreRotated(selected, error.RotatedCredential);
                throw;
            }
        }
        if (!OwnedProviderAccess.HasKey(id) && !OwnedProviderAccess.HasLocalEndpoint(id))
            return new(id, ReadingState.Unsupported, [], Message: OwnedProviderAccess.Unavailable);
        var credential = OwnedCredential(id);
        if (id is "factory" or "groq" && credential is not null && !OwnedProviderAccess.IsPlainApiKey(credential)) credential = null;
        if (credential is null && !OwnedProviderAccess.HasLocalEndpoint(id))
            return new(id, ReadingState.NeedsAuth, [], Message: id is "copilot" or "gemini"
                ? "Sign in or supply your credential in CodeRim Settings." : "Enter an API key in CodeRim Settings.");
        string? Setting(string name) => name switch
        {
            "DEEPSEEK_USAGE_SOURCE" or "KIMI_USAGE_SOURCE" or "AMP_USAGE_SOURCE" or "ALIBABA_CODING_PLAN_SOURCE" or "MINIMAX_USAGE_SOURCE" => "api",
            "ANTIGRAVITY_USAGE_SOURCE" => "oauth",
            _ => OwnedSetting(id, name)
        };
        if (ScriptProviders.Catalog.TryGetValue(id, out var script))
        {
            var keys = OwnedProviderAccess.CredentialKeys(id);
            return await scripts.FetchAsync(id, name => keys.Contains(name, StringComparer.Ordinal) ? Setting(name) ?? credential : Setting(name),
                null, null, token).ConfigureAwait(false);
        }
        if (NativeProviders.Supported.Contains(id))
            return await native.FetchAsync(id, credential, Setting, token).ConfigureAwait(false);
        return await http.FetchAsync(id, credential, Setting, token).ConfigureAwait(false);
    }
}
