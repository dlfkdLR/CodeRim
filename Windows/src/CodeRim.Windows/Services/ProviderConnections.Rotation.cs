using CodeRim.Core.Domain;

namespace CodeRim.Windows.Services;

internal sealed record ProviderScopeRotation(string SourceScope, string TargetScope, string Key, string Version);
internal sealed record ProviderFetchResult(ProviderReading Reading, ProviderScopeRotation? Rotation);

internal sealed partial class ProviderConnections
{
    private sealed record ScopeOverride(string Key, CredentialVault.Snapshot Snapshot);
    private sealed class RotationCapture(string? sourceScope)
    {
        internal string? SourceScope { get; } = sourceScope;
        internal ProviderScopeRotation? Accepted { get; set; }
    }
    internal async Task<ProviderFetchResult> FetchForStoreAsync(string id, AppSettings settings, string? sourceScope, CancellationToken token)
    {
        var capture = new RotationCapture(sourceScope);
        var reading = await FetchAsync(id, settings, null, capture, token).ConfigureAwait(false);
        return new(reading, capture.Accepted);
    }
    internal bool TryAcceptScopeRotation(string id, string? sourceScope, ProviderScopeRotation? rotation, out string? targetScope)
    {
        targetScope = null;
        if (id != "factory" || rotation is null || sourceScope is null || sourceScope != rotation.SourceScope
            || vault.Version(rotation.Key) != rotation.Version || Scope(id) != rotation.TargetScope) return false;
        targetScope = rotation.TargetScope; return true;
    }
    private bool SaveFactoryRotation(string key, CredentialVault.Snapshot original, string updated, string? sourceScope,
        RotationCapture? capture, out ProviderScopeRotation? accepted, CancellationToken token)
    {
        accepted = null;
        if (token.IsCancellationRequested || sourceScope is null || capture is not null && capture.SourceScope != sourceScope
            || Scope("factory") != sourceScope || Scope("factory", new(key, original)) != sourceScope) return false;
        if (!vault.SaveIfUnchanged(key, original.Version, updated, out var version) || version is null) return false;
        // Re-project only our replacement field. Every other setting/account input must
        // still produce the starting scope; a concurrent source change cannot be adopted.
        var target = Scope("factory", new(key, new(updated, version)));
        if (target is null || Scope("factory", new(key, original)) != sourceScope
            || Scope("factory") != target || vault.Version(key) != version) return false;
        accepted = new(sourceScope, target, key, version);
        if (capture is not null) capture.Accepted = accepted;
        return true;
    }
}
