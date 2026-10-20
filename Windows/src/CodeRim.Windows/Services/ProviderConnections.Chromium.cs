using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal sealed partial class ProviderConnections
{
    internal static bool ChromiumEnabled(CredentialVault vault, string id) => id switch
    {
        "deepseek" => DeepSeekAuthentication.Source(EffectiveSetting(vault, id, "DEEPSEEK_USAGE_SOURCE")) is "auto" or "web",
        "minimax" => MiniMaxAuthentication.Source(EffectiveSetting(vault, id, "MINIMAX_USAGE_SOURCE")) is "auto" or "web",
        "factory" => true,
        _ => false
    };
    internal static string ChromiumRegion(CredentialVault vault, string id) => id == "minimax"
        ? MiniMaxAuthentication.Region(EffectiveSetting(vault, id, "MINIMAX_REGION")) ?? throw new InvalidDataException("Select a MiniMax region.") : "default";
    internal static string ChromiumStorageKey(CredentialVault vault, string id) => "chromium:" + id + ":" + ChromiumRegion(vault, id);
    internal static string ChromiumSelection(CredentialVault vault, string id) => JsonSerializer.Serialize(new { id,
        region = ChromiumRegion(vault, id), source = id == "deepseek" ? EffectiveSetting(vault, id, "DEEPSEEK_USAGE_SOURCE") : id == "minimax" ? EffectiveSetting(vault, id, "MINIMAX_USAGE_SOURCE") : null,
        details = id == "deepseek" ? EffectiveSetting(vault, id, "DEEPSEEK_DETAILED_USAGE") : null });
    private string? ChromiumScope(string id, ScopeOverride? replacement)
    {
        if (!ChromiumEnabled(vault, id)) return null;
        var key = ChromiumStorageKey(vault, id);
        var snapshot = replacement?.Key == key ? replacement.Snapshot : vault.LoadVersioned(key);
        if (snapshot is null) return null;
        var raw = snapshot.Value;
        var credential = ChromiumProviderCredential.Parse(raw);
        if (credential.Provider != id || credential.Region != ChromiumRegion(vault, id)) throw new InvalidDataException("The imported connection has another region.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ChromiumSelection(vault, id) + raw + snapshot.Version)));
    }
    internal Task<ProviderReading> VerifyChromiumAsync(ChromiumProviderCredential credential, Func<ChromiumProviderCredential, bool> saveRotated, CancellationToken token)
        => ReadChromiumAsync(credential, false, saveRotated, token);
    private Task<ProviderReading> ReadChromiumAsync(ChromiumProviderCredential credential, bool detailed, Func<ChromiumProviderCredential, bool> saveRotated, CancellationToken token)
        => credential.Provider switch
        {
            "deepseek" => http.FetchAsync("deepseek", credential.Secret,
                key => key == "DEEPSEEK_USAGE_SOURCE" ? "web" : key == "DEEPSEEK_DETAILED_USAGE" ? detailed.ToString() : null, token),
            "factory" => native.FetchFactorySessionAsync(credential.Secret!, _ => null,
                updated => saveRotated(credential with { Secret = updated }), token),
            "minimax" => native.FetchMiniMaxWebAsync(credential.MiniMax(), token),
            _ => throw new InvalidDataException("Unsupported imported sign-in.")
        };
    private async Task<ProviderReading?> FetchChromiumAsync(string id, RotationCapture? capture, CancellationToken token)
    {
        if (!ChromiumEnabled(vault, id)) return null;
        var key = ChromiumStorageKey(vault, id); var setting = ChromiumSelection(vault, id);
        var snapshot = vault.LoadVersioned(key); if (snapshot is null) return null;
        var credential = ChromiumProviderCredential.Parse(snapshot.Value);
        if (credential.Provider != id || credential.Region != ChromiumRegion(vault, id)) throw new InvalidDataException("Imported provider mismatch.");
        using var lease = id == "factory" ? await vault.AcquireRefreshAsync("chromium-factory", token).ConfigureAwait(false) : null;
        if (vault.Version(key) != snapshot.Version || ChromiumSelection(vault, id) != setting) return ChangedChromium(id);
        var sourceScope = Scope(id); ProviderScopeRotation? rotation = null;
        var reading = await ReadChromiumAsync(credential,
            id == "deepseek" && DeepSeekUsageDetails.Enabled(EffectiveSetting(vault, id, "DEEPSEEK_DETAILED_USAGE")), updated =>
            {
                if (ChromiumSelection(vault, id) != setting) return false;
                return SaveFactoryRotation(key, snapshot, updated.Serialize(), sourceScope, capture, out rotation, token);
            }, token).ConfigureAwait(false);
        return ChromiumSelection(vault, id) == setting && vault.Version(key) == (rotation?.Version ?? snapshot.Version)
            && Scope(id) == (rotation?.TargetScope ?? sourceScope) ? reading : ChangedChromium(id);
    }
    private static ProviderReading ChangedChromium(string id) => new(id, ReadingState.Unavailable, [], Message: "The selected sign-in changed. Refresh the current connection.");
}
