using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal sealed partial class ProviderConnections
{
    private readonly object kimiSelectionLock = new();
    private sealed record KimiFallback(KimiCredential[] Candidates, KimiCredential Selected, DateTimeOffset Until);
    private KimiFallback? kimiFallback;
    private string? KimiSetting(string key) => EffectiveSetting(vault, "kimi", key);
    private string? KimiApi() => new[] { vault.Load("provider:kimi"), Environment.GetEnvironmentVariable("KIMI_CODE_API_KEY") }
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private KimiCredential? KimiCli() => KimiAuthentication.AllowsCli(KimiSetting)
        ? KimiAuthentication.LocalProfile(readCredential("kimi"), DateTimeOffset.UtcNow) : null;
    private KimiCredential KimiWeb(BrowserCookieJar? browserOverride = null)
    {
        var jar = browserOverride ?? BrowserConnections.Load("kimi", vault);
        if (jar is not null) return new("web", KimiAuthentication.WebToken(jar.Header(NativeProviders.KimiWebUsageUri, DateTimeOffset.UtcNow)), BrowserState: jar.Serialize());
        var raw = new[] { vault.Load("cookie:kimi"), Environment.GetEnvironmentVariable("KIMI_MANUAL_COOKIE"),
            Environment.GetEnvironmentVariable("KIMI_AUTH_TOKEN"), Environment.GetEnvironmentVariable("kimi_auth_token") }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return new("web", KimiAuthentication.WebToken(raw));
    }
    private KimiCredential[] KimiCandidates()
    {
        var candidates = new List<KimiCredential>();
        var api = KimiApi();
        if (!string.IsNullOrWhiteSpace(api)) candidates.Add(new("api", KimiAuthentication.ApiKey(api), KimiSetting("KIMI_CODE_BASE_URL")));
        try { if (KimiCli() is { } cli) candidates.Add(cli); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or CryptographicException) { }
        try { candidates.Add(KimiWeb()); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or CryptographicException) { }
        return candidates.ToArray();
    }
    private KimiCredential? ResolveKimi(BrowserCookieJar? browserOverride = null)
    {
        if (browserOverride is not null) return KimiWeb(browserOverride);
        var mode = KimiAuthentication.Source(KimiSetting("KIMI_USAGE_SOURCE"));
        if (mode is null) return null;
        lock (kimiSelectionLock)
        {
            if (mode == "auto" && kimiFallback is { } cached)
            {
                if (cached.Until > DateTimeOffset.UtcNow && cached.Candidates.SequenceEqual(KimiCandidates())) return cached.Selected;
                kimiFallback = null;
            }
        }
        return KimiAuthentication.Resolve(mode, KimiApi, KimiCli, () => KimiWeb(), KimiSetting);
    }
    private async Task<ProviderReading> FetchKimiConnectionAsync(BrowserCookieJar? browserOverride, CancellationToken token)
    {
        var selected = ResolveKimi(browserOverride);
        if (selected is null) return new("kimi", ReadingState.Error, [], Message: "Choose Auto, API or Web as the Kimi source.");
        var mode = KimiAuthentication.Source(KimiSetting("KIMI_USAGE_SOURCE"));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(30));
        ProviderReading Changed() => new("kimi", ReadingState.Unavailable, [], Message: "The Kimi connection changed. Refresh the selected account.");
        try
        {
            var outcome = await native.FetchKimiResultAsync(selected, budget.Token).ConfigureAwait(false);
            var reading = outcome.Reading;
            if (selected != ResolveKimi(browserOverride) || mode != KimiAuthentication.Source(KimiSetting("KIMI_USAGE_SOURCE"))) return Changed();
            if (browserOverride is not null || mode != "auto" || !outcome.CanFallback || reading.State is ReadingState.Ready or ReadingState.Partial) return reading;
            var candidates = KimiCandidates();
            var index = Array.FindIndex(candidates, candidate => candidate == selected);
            if (index < 0) return Changed();
            foreach (var fallback in candidates.Skip(index + 1).Where(candidate => candidate.Token is not null))
            {
                budget.Token.ThrowIfCancellationRequested();
                if (mode != KimiAuthentication.Source(KimiSetting("KIMI_USAGE_SOURCE")) || !candidates.SequenceEqual(KimiCandidates())) return Changed();
                var fallbackResult = await native.FetchKimiResultAsync(fallback, budget.Token).ConfigureAwait(false);
                var next = fallbackResult.Reading;
                if (mode != KimiAuthentication.Source(KimiSetting("KIMI_USAGE_SOURCE")) || !candidates.SequenceEqual(KimiCandidates())) return Changed();
                if (!fallbackResult.CanFallback) return next;
                if (next.State is not (ReadingState.Ready or ReadingState.Partial)) continue;
                // A short lease lets DashboardStore stabilize under the selected account's exact scope.
                // It expires before the next ordinary minute poll so the preferred source can recover.
                lock (kimiSelectionLock) kimiFallback = new(candidates, fallback, DateTimeOffset.UtcNow.AddSeconds(30));
                return next;
            }
            lock (kimiSelectionLock) kimiFallback = null;
            return reading;
        }
        catch (OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return new("kimi", ReadingState.Error, [], Message: "Kimi refresh timed out.");
        }
    }
}
