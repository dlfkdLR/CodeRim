using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal sealed partial class ProviderConnections
{
    private sealed record AlibabaChoice(string Mode, string Region, string? ConfiguredPath, string? Executable,
        string? Browser, string? Cookie, string? SecToken);
    internal static string? AlibabaSource(CredentialVault vault)
    {
        const string key = "setting:alibabatokenplan:ALIBABA_TOKEN_PLAN_SOURCE";
        var snapshot = vault.LoadVersioned(key);
        var configured = snapshot?.Value.Trim() is { Length: > 0 } saved ? saved
            : Environment.GetEnvironmentVariable("ALIBABA_TOKEN_PLAN_SOURCE")?.Trim();
        if (!string.IsNullOrEmpty(configured)) return AlibabaTokenPlanCliUsage.Source(configured);
        // Bind the absent/blank value and its version in one read. A later explicit
        // selection must not become the expected version of this migration.
        var version = snapshot?.Version;
        var mode = vault.Version("browser:alibabatokenplan") is not null || vault.Version("provider:alibabatokenplan") is not null
            || Environment.GetEnvironmentVariable("ALIBABA_TOKEN_PLAN_COOKIE") is { Length: > 0 }
            || EffectiveSetting(vault, "alibabatokenplan", "ALIBABA_TOKEN_PLAN_SEC_TOKEN") is not null ? "web" : "auto";
        // Persist the initial choice, including Web after removal of its credentials.
        // A concurrently saved explicit selection always wins this migration.
        vault.SaveIfUnchanged(key, version, mode);
        return AlibabaTokenPlanCliUsage.Source(EffectiveSetting(vault, "alibabatokenplan", "ALIBABA_TOKEN_PLAN_SOURCE"));
    }
    private AlibabaChoice? ResolveAlibaba(BrowserCookieJar? browserOverride = null)
    {
        var mode = browserOverride is not null ? "web" : AlibabaSource(vault);
        var region = AlibabaTokenPlanCliUsage.Region(EffectiveSetting(vault, "alibabatokenplan", "ALIBABA_TOKEN_PLAN_REGION"));
        if (mode is null || region is null) return null;
        var path = mode == "web" ? null : EffectiveSetting(vault, "alibabatokenplan", "ALIBABA_TOKEN_PLAN_EXECUTABLE");
        var executable = mode == "web" ? null : AlibabaTokenPlanCliUsage.ResolveExecutable(path, Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        BrowserCookieJar? browser = null; string? cookie = null; string? secToken = null;
        if (mode != "cli")
        {
            try
            {
                browser = browserOverride ?? BrowserConnections.Load("alibabatokenplan", vault);
                cookie = browser is not null ? null : vault.Load("provider:alibabatokenplan")
                    ?? Environment.GetEnvironmentVariable("ALIBABA_TOKEN_PLAN_COOKIE");
                secToken = EffectiveSetting(vault, "alibabatokenplan", "ALIBABA_TOKEN_PLAN_SEC_TOKEN");
            }
            catch (Exception error) when (mode == "auto" && error is IOException or InvalidDataException
                or JsonException or UnauthorizedAccessException or CryptographicException)
            {
                // A damaged optional Web connection must not block a valid CLI.
                // Discard the entire Web candidate rather than borrowing another account.
                browser = null; cookie = null; secToken = null;
            }
        }
        return new(mode, region, path, executable, browser?.Serialize(), cookie, secToken);
    }
    private string? AlibabaScope()
    {
        var selected = ResolveAlibaba();
        return selected is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { selected, process = selected.Mode == "web" ? 0 : Environment.ProcessId }))));
    }
    private async Task<ProviderReading> FetchAlibabaConnectionAsync(BrowserCookieJar? browserOverride, CancellationToken token)
    {
        const string id = "alibabatokenplan";
        var selected = ResolveAlibaba(browserOverride);
        if (selected is null) return new(id, ReadingState.Error, [], Message: "Choose Auto, CLI or Web and a Token Plan region.");
        ProviderReading Changed() => new(id, ReadingState.Unavailable, [], Message: "The Token Plan connection changed. Refresh the selected source.");
        bool Current() => selected == ResolveAlibaba(browserOverride);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(35));
        ProviderReading? cli = null;
        if (selected.Mode != "web")
        {
            cli = selected.Executable is null
                ? new(id, ReadingState.NeedsAuth, [], Message: "Install Bailian CLI, sign in with bl login, and select bl.exe if needed.")
                : await readAlibabaCli(selected.Executable, selected.Region, budget.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!Current()) return Changed();
            if (selected.Mode == "cli" || cli.State == ReadingState.Ready) return cli;
        }
        if (selected.Browser is null && string.IsNullOrWhiteSpace(selected.Cookie))
            return cli ?? new(id, ReadingState.NeedsAuth, [], Message: "Connect a Token Plan Web session in Settings.");
        if (!Current()) return Changed();
        var jar = selected.Browser is null ? null : BrowserCookieJar.Parse(selected.Browser, BrowserConnections.Domains(id));
        string? Setting(string key) => key == "ALIBABA_TOKEN_PLAN_REGION" ? selected.Region
            : key == "ALIBABA_TOKEN_PLAN_SEC_TOKEN" ? selected.SecToken : null;
        var reading = await native.FetchAsync(id, selected.Cookie, Setting,
            jar is null ? null : uri => jar.Header(uri, DateTimeOffset.UtcNow), budget.Token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!Current()) return Changed();
        return reading with { Message = reading.State is ReadingState.Ready or ReadingState.Partial
            ? "Source: Web" + (cli is null ? "" : " · CLI unavailable; selected Web session")
            : cli is null ? reading.Message : "CLI and Web could not read usage. Check each selected sign-in and refresh." };
    }
}
