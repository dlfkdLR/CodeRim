using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal sealed partial class ProviderConnections
{
    private sealed record MiniMaxChoice(string Mode, string Region, string Source, string? Raw, MiniMaxWebCredential? Web);
    internal static void BindMiniMaxLegacy(CredentialVault vault)
    {
        // Bind the legacy slot before a region change without decrypting an unrelated API credential.
        if (vault.Load("setting:minimax:LEGACY_REGION") is null
            && MiniMaxAuthentication.Region(EffectiveSetting(vault, "minimax", "MINIMAX_REGION")) is { } region)
            vault.Save("setting:minimax:LEGACY_REGION", region);
    }
    private MiniMaxChoice? ResolveMiniMax(BrowserCookieJar? browserOverride = null)
    {
        var region = MiniMaxAuthentication.Region(EffectiveSetting(vault, "minimax", "MINIMAX_REGION"));
        var mode = MiniMaxAuthentication.Source(EffectiveSetting(vault, "minimax", "MINIMAX_USAGE_SOURCE"));
        if (region is null || mode is null) return null;
        BindMiniMaxLegacy(vault);
        var environmentRegion = MiniMaxAuthentication.Region(Environment.GetEnvironmentVariable("MINIMAX_REGION"));
        if (browserOverride is not null || mode != "api")
        {
            var jar = browserOverride ?? BrowserConnections.Load("minimax", vault);
            if (jar is not null)
            {
                var raw = jar.Header(MiniMaxAuthentication.PlanUri(region), DateTimeOffset.UtcNow);
                var web = MiniMaxAuthentication.Parse(raw, region);
                return new(mode, region, "browser", jar.Serialize(), web is null ? null : web with { BrowserState = jar.Serialize() });
            }
            var manual = vault.Load("cookie:minimax:" + region)
                ?? (environmentRegion == region ? Environment.GetEnvironmentVariable("MINIMAX_COOKIE") ?? Environment.GetEnvironmentVariable("MINIMAX_COOKIE_HEADER") : null);
            if (mode == "web" || manual is not null) return new(mode, region, "web", manual, MiniMaxAuthentication.Parse(manual, region));
        }
        var api = vault.Load("provider:minimax:" + region)
            ?? (vault.Load("setting:minimax:LEGACY_REGION") == region ? vault.Load("provider:minimax") : null)
            ?? (environmentRegion == region ? Environment.GetEnvironmentVariable("MINIMAX_CODING_API_KEY") ?? Environment.GetEnvironmentVariable("MINIMAX_API_KEY") : null);
        return new(mode, region, "api", api, null);
    }
    private string? MiniMaxScope()
    {
        var choice = ResolveMiniMax();
        return choice is null || choice.Raw is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(choice))));
    }
    private async Task<ProviderReading> FetchMiniMaxConnectionAsync(BrowserCookieJar? browserOverride, CancellationToken token)
    {
        var selected = ResolveMiniMax(browserOverride);
        if (selected is null) return new("minimax", ReadingState.Error, [], Message: "Choose Auto, API or Web and a MiniMax region.");
        var reading = selected.Source == "api"
            ? await native.FetchAsync("minimax", selected.Raw, key => key == "MINIMAX_REGION" ? selected.Region : key == "MINIMAX_USAGE_SOURCE" ? "api" : null, token).ConfigureAwait(false)
            : await native.FetchMiniMaxWebAsync(selected.Web, token).ConfigureAwait(false);
        return selected == ResolveMiniMax(browserOverride) ? reading
            : new("minimax", ReadingState.Unavailable, [], Message: "The MiniMax connection changed. Refresh the selected region and account.");
    }
}
