using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal sealed partial class ProviderConnections
{
    private sealed record AlibabaCodingChoice(string? Mode, string? Region, string? ApiRegion, string? Api, string? Cookie, string? Browser);
    internal static string? AlibabaCodingSource(CredentialVault vault) => AlibabaCodingPlanAuthentication.Source(EffectiveSetting(vault, "alibaba", "ALIBABA_CODING_PLAN_SOURCE"));
    internal static string? AlibabaCodingRegion(CredentialVault vault) => AlibabaCodingPlanAuthentication.Region(EffectiveSetting(vault, "alibaba", "ALIBABA_CODING_PLAN_REGION"))?.Name;
    internal static string AlibabaCodingWebKey(CredentialVault vault) => "cookie:alibaba:" + (AlibabaCodingRegion(vault) ?? "invalid");
    internal static string AlibabaCodingBrowserKey(CredentialVault vault) => "browser:alibaba:" + (AlibabaCodingRegion(vault) ?? "invalid");
    private AlibabaCodingChoice ResolveAlibabaCoding(BrowserCookieJar? browserOverride = null)
    {
        var mode = browserOverride is null ? AlibabaCodingSource(vault) : "web";
        var configuredRegion = EffectiveSetting(vault, "alibaba", "ALIBABA_CODING_PLAN_REGION");
        var region = AlibabaCodingPlanAuthentication.Region(configuredRegion)?.Name;
        if (mode is null || region is null) return new(mode, region, configuredRegion, null, null, null);
        if (mode == "api")
        {
            var api = vault.Load("provider:alibaba");
            foreach (var key in NativeProviders.CredentialKeys("alibaba")!) api ??= Environment.GetEnvironmentVariable(key)?.Trim() is { Length: > 0 } value ? value : null;
            return new(mode, region, configuredRegion, api, null, null);
        }
        var state = browserOverride?.Serialize() ?? vault.Load("browser:alibaba:" + region);
        if (state is not null)
        {
            var jar = BrowserCookieJar.Parse(state, AlibabaCodingPlanAuthentication.Domains(region));
            return new(mode, region, configuredRegion, null, null, jar.Serialize());
        }
        var environmentRegion = AlibabaCodingPlanAuthentication.Region(Environment.GetEnvironmentVariable("ALIBABA_CODING_PLAN_REGION"))?.Name;
        var cookie = vault.Load("cookie:alibaba:" + region) ?? (environmentRegion == region ? Environment.GetEnvironmentVariable("ALIBABA_CODING_PLAN_COOKIE") : null);
        return new(mode, region, configuredRegion, null, cookie, null);
    }
    private string? AlibabaCodingScope()
    {
        var selected = ResolveAlibabaCoding();
        return selected.Mode is null || selected.Region is null || selected.Api is null && selected.Cookie is null && selected.Browser is null ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(selected))));
    }
    private async Task<ProviderReading> FetchAlibabaCodingConnectionAsync(BrowserCookieJar? browserOverride, CancellationToken token)
    {
        const string id = "alibaba";
        var selected = ResolveAlibabaCoding(browserOverride);
        if (selected.Mode is null || selected.Region is null) return new(id, ReadingState.Error, [], Message: "Choose API or Web and an International or China region.");
        ProviderReading Changed() => new(id, ReadingState.Unavailable, [], Message: "The Coding Plan source, region or session changed. Refresh the selected account.");
        if (selected != ResolveAlibabaCoding(browserOverride)) return Changed();
        var jar = selected.Browser is null ? null : BrowserCookieJar.Parse(selected.Browser, AlibabaCodingPlanAuthentication.Domains(selected.Region));
        var reading = selected.Mode == "api"
            ? await native.FetchAsync(id, selected.Api, key => key == "ALIBABA_CODING_PLAN_SOURCE" ? "api" : key == "ALIBABA_CODING_PLAN_REGION" ? selected.ApiRegion : null, token).ConfigureAwait(false)
            : await native.FetchAlibabaCodingWebAsync(selected.Cookie, selected.Region, jar is null ? null : uri => jar.Header(uri, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return selected == ResolveAlibabaCoding(browserOverride) ? reading : Changed();
    }
}
