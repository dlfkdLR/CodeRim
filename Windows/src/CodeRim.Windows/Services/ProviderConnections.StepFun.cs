using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal sealed partial class ProviderConnections
{
    private sealed record StepFunChoice(string Mode, string Source, StepFunCredential? Profile, string? Version = null, string? BrowserState = null, string? Raw = null)
    {
        public string? Scope => Profile is null ? null : StepFunAuthentication.Hash(JsonSerializer.Serialize(new { Mode, Source,
            Owner = StepFunAuthentication.Scope(Profile), BrowserState }));
    }
    private string? stepFunSessionScope;
    private string? stepFunSession;
    private StepFunChoice ResolveStepFun(BrowserCookieJar? browserOverride = null)
    {
        var mode = StepFunAuthentication.Mode(EffectiveSetting(vault, "stepfun", "STEPFUN_AUTH_MODE"));
        if (mode is null) return new("invalid", "none", null);
        if (browserOverride is not null) return Browser(browserOverride);
        StepFunChoice Browser(BrowserCookieJar jar) => new(mode, "browser",
            StepFunAuthentication.Manual(jar.Header(NativeProviders.StepFunUsageUri, DateTimeOffset.UtcNow)), BrowserState: jar.Serialize());
        if (mode == "auto" && BrowserConnections.Load("stepfun", vault) is { } browser) return Browser(browser);
        var saved = vault.LoadVersioned("provider:stepfun");
        if (saved is not null)
        {
            var profile = StepFunAuthentication.Saved(saved.Value);
            if (mode == "manual" && profile?.Kind != "manual") profile = null;
            return new(mode, "saved", profile, saved.Version, Raw: saved.Value);
        }
        if (mode == "manual") return new(mode, "manual", null);
        var username = vault.Load("setting:stepfun:STEPFUN_USERNAME");
        var password = vault.Load("setting:stepfun:STEPFUN_PASSWORD");
        if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrEmpty(password))
            return new(mode, "settings-login", StepFunAuthentication.Login(username, password));
        var envToken = Environment.GetEnvironmentVariable("STEPFUN_TOKEN");
        var login = StepFunAuthentication.Login(Environment.GetEnvironmentVariable("STEPFUN_USERNAME"), Environment.GetEnvironmentVariable("STEPFUN_PASSWORD"));
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            var manual = StepFunAuthentication.Manual(envToken);
            var profile = manual is null ? null : login is null ? manual : login with { Token = manual.Token, Owner = StepFunAuthentication.Hash(manual.Owner + login.Owner) };
            return new(mode, "environment-token", profile);
        }
        return new(mode, "environment-login", login);
    }
    private async Task<ProviderReading> FetchStepFunConnectionAsync(BrowserCookieJar? browserOverride, CancellationToken token)
    {
        var initial = ResolveStepFun(browserOverride);
        ProviderReading Changed() => new("stepfun", ReadingState.Unavailable, [], Message: "The StepFun connection changed. Refresh the selected account.");
        if (initial.Mode == "invalid") return new("stepfun", ReadingState.Error, [], Message: "Choose Auto or Manual as the StepFun authentication mode.");
        if (initial.Profile is null) return new("stepfun", ReadingState.NeedsAuth, [], Message: "Save a StepFun username and password, or select a manual Oasis-Token.");
        using var lease = await vault.AcquireRefreshAsync("stepfun", token).ConfigureAwait(false);
        var selected = ResolveStepFun(browserOverride);
        if (initial != selected) return Changed();
        var scope = selected.Scope;
        var effective = selected.Profile!;
        if (browserOverride is null && stepFunSessionScope == scope && stepFunSession is not null) effective = effective with { Token = stepFunSession };
        var jar = selected.BrowserState is null ? null : BrowserCookieJar.Parse(selected.BrowserState, ["stepfun.com"]);
        string? Cookie(Uri uri) => jar?.Header(uri, DateTimeOffset.UtcNow);
        var result = await native.FetchStepFunAsync(effective, jar is null ? null : Cookie, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (selected != ResolveStepFun(browserOverride)) return Changed();
        if (result.Token is not null && result.Token != selected.Profile!.Token)
        {
            if (selected.Source == "saved")
            {
                var updated = selected.Profile with { Token = result.Token };
                if (!vault.SaveIfUnchanged("provider:stepfun", selected.Version!, JsonSerializer.Serialize(updated))) return Changed();
                var current = ResolveStepFun();
                if (current.Scope != scope || current.Profile?.Token != result.Token) return Changed();
            }
            else if (browserOverride is null)
            {
                stepFunSessionScope = scope; stepFunSession = result.Token;
            }
        }
        return result.Reading;
    }
}
