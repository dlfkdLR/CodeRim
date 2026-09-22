using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static readonly string[] AlibabaEnvironmentKeys = ["ALIBABA_TOKEN_PLAN_SOURCE", "ALIBABA_TOKEN_PLAN_REGION",
        "ALIBABA_TOKEN_PLAN_EXECUTABLE", "ALIBABA_TOKEN_PLAN_COOKIE", "ALIBABA_TOKEN_PLAN_SEC_TOKEN"];
    private static async Task AlibabaConnectionRegression(DashboardWindow owner, AppSettingsStore settings, CredentialVault vault, string directory)
    {
        const string id = "alibabatokenplan";
        var enabled = settings.Current.EnabledProviders;
        var environment = AlibabaEnvironmentKeys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var keys = AlibabaEnvironmentKeys.Select(key => "setting:" + id + ":" + key)
            .Concat(["provider:" + id, "browser:" + id]).ToArray();
        var previous = keys.ToDictionary(key => key, vault.Load);
        var fixtureDirectory = Path.Combine(directory, "bailian executable fixture");
        Directory.CreateDirectory(fixtureDirectory);
        var executable = Path.Combine(fixtureDirectory, "bl.exe");
        File.WriteAllText(executable, "not executable; only the injected reader may use this fixture path");
        var cliCalls = 0; var httpCalls = 0; var cliFailure = false; var changeDuringCli = false;
        Task<ProviderReading> Cli(string path, string region, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); cliCalls++;
            Require(path == executable && region is "intl" or "cn" or "intl-personal" or "cn-personal", "Alibaba CLI selection was not fixed.");
            if (changeDuringCli) { changeDuringCli = false; vault.Save("setting:" + id + ":ALIBABA_TOKEN_PLAN_SOURCE", "web"); }
            return Task.FromResult(cliFailure ? new ProviderReading(id, ReadingState.Error, [], Message: "Synthetic CLI failure.")
                : AlibabaTokenPlanCliUsage.Parse(new(0, """{"per5HourPercentage":0.25,"per1WeekPercentage":0.5}""", "")));
        }
        HttpResponseMessage Web(HttpRequestMessage request)
        {
            httpCalls++;
            Require(request.RequestUri!.Host.EndsWith(".alibabacloud.com", StringComparison.Ordinal) || request.RequestUri.Host.EndsWith(".aliyun.com", StringComparison.Ordinal),
                "Alibaba Web escaped its regional origin.");
            Require(request.Headers.Authorization is null && request.Headers.TryGetValues("Cookie", out _), "Alibaba Web did not use its separate cookie.");
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"data":{"per5HourPercentage":0.4,"per1WeekPercentage":0.6}}""") };
        }
        ProviderConnections Connections() => new(vault, new NativeProviders(new AmpFixtureHandler(Web)), nativeCredentialReader: _ => null, alibabaCliReader: Cli);
        try
        {
            foreach (var key in AlibabaEnvironmentKeys) Environment.SetEnvironmentVariable(key, null);
            foreach (var key in keys) vault.Delete(key);
            vault.Save("provider:" + id, "session=synthetic-web; sec_token=synthetic-csrf");
            vault.Save("setting:" + id + ":ALIBABA_TOKEN_PLAN_SEC_TOKEN", "synthetic-csrf");
            vault.Save("setting:" + id + ":ALIBABA_TOKEN_PLAN_EXECUTABLE", executable);
            using var verifier = Connections();
            Require(ProviderConnections.AlibabaSource(vault) == "web" && verifier.CanCache(id), "Upgrade silently selected a CLI account.");
            var initial = await verifier.FetchAsync(id, settings.Current, CancellationToken.None);
            Require(initial.Headline?.UsedPercent == 40 && cliCalls == 0, "Legacy Alibaba Web connection was not preserved.");
            vault.Delete("provider:" + id); vault.Delete("setting:" + id + ":ALIBABA_TOKEN_PLAN_SEC_TOKEN");
            Require(ProviderConnections.AlibabaSource(vault) == "web" &&
                (await verifier.FetchAsync(id, settings.Current, CancellationToken.None)).State == ReadingState.NeedsAuth && cliCalls == 0,
                "Removing a legacy Web connection silently selected the CLI account.");
            vault.Save("provider:" + id, "session=synthetic-web; sec_token=synthetic-csrf");
            vault.Save("setting:" + id + ":ALIBABA_TOKEN_PLAN_SEC_TOKEN", "synthetic-csrf");
            vault.Save("setting:" + id + ":ALIBABA_TOKEN_PLAN_SOURCE", "cli");
            var before = httpCalls;
            foreach (var region in new[] { "intl", "cn", "intl-personal", "cn-personal" })
            {
                vault.Save("setting:" + id + ":ALIBABA_TOKEN_PLAN_REGION", region);
                Require((await verifier.FetchAsync(id, settings.Current, CancellationToken.None)).Headline?.UsedPercent == 25, "A CLI region was not connected.");
            }
            Require(httpCalls == before && !verifier.CanCache(id), "Explicit CLI fetched Web or enabled accountless disk cache.");
            cliFailure = true;
            Require((await verifier.FetchAsync(id, settings.Current, CancellationToken.None)).State == ReadingState.Error && httpCalls == before,
                "Failed explicit CLI silently used another source.");
            vault.Save("setting:" + id + ":ALIBABA_TOKEN_PLAN_SOURCE", "auto");
            Require((await verifier.FetchAsync(id, settings.Current, CancellationToken.None)).Headline?.UsedPercent == 40 && !verifier.CanCache(id),
                "Auto did not fall back to the selected Web connection without caching it.");
            cliFailure = false; before = httpCalls;
            foreach (var malformed in new[] { "{", "null", "{}" })
            {
                vault.Save("browser:" + id, malformed);
                Require((await verifier.FetchAsync(id, settings.Current, CancellationToken.None)).Headline?.UsedPercent == 25
                    && httpCalls == before, "An unselected damaged Web connection blocked the valid CLI.");
                cliFailure = true;
                Require((await verifier.FetchAsync(id, settings.Current, CancellationToken.None)).State == ReadingState.Error
                    && httpCalls == before, "A damaged selected Web jar borrowed the manual cookie account.");
                cliFailure = false;
            }
            vault.Delete("browser:" + id);
            cliFailure = false; changeDuringCli = true; before = httpCalls;
            Require((await verifier.FetchAsync(id, settings.Current, CancellationToken.None)).State == ReadingState.Unavailable && httpCalls == before,
                "Late CLI response survived a source change.");
            vault.Save("setting:" + id + ":ALIBABA_TOKEN_PLAN_SOURCE", "cli");
            vault.Save("setting:" + id + ":ALIBABA_TOKEN_PLAN_REGION", "intl-personal");
            var jar = new BrowserCookieJar([new("session", "synthetic-browser", ".alibabacloud.com", "/", true, false, 0)], ["alibabacloud.com"]);
            var priorCli = cliCalls;
            Require((await verifier.VerifyBrowserAsync(id, settings.Current, jar)).Headline?.UsedPercent == 40 && cliCalls == priorCli,
                "Browser verification executed the selected CLI.");
            settings.Save(settings.Current with { EnabledProviders = [id] });
            using var store = new DashboardStore(settings, vault, providerConnections: Connections());
            var window = new DashboardWindow(store, settings, vault); window.Show(); window.Navigate(id);
            try
            {
                await store.RefreshProviderAsync(id); await Idle();
                Require(store.Readings[id].Headline?.UsedPercent == 25, "CLI quota did not reach the actual store.");
                Require(!Descendants<PasswordBox>(window).Any() && !Descendants<Button>(window).Any(button => Equals(button.Content, "Import from Firefox…")),
                    "CLI view exposes inactive Web credentials.");
                Capture(window, Path.Combine(directory, "windows-alibaba-cli.png"));
                cliFailure = true; await store.RefreshProviderAsync(id); await Idle();
                Require(store.Readings[id].Windows.Count == 0, "CLI failure retained old accountless quota.");
                var picker = Descendants<ComboBox>(window).Single(combo => AutomationProperties.GetName(combo) == "Usage source");
                picker.SelectedItem = "Web"; await Idle(); await store.RefreshProviderAsync(id); await Idle();
                Require(vault.Load("setting:" + id + ":ALIBABA_TOKEN_PLAN_SOURCE") == "web" && store.Readings[id].Headline?.UsedPercent == 40
                    && Descendants<Button>(window).Any(button => Equals(button.Content, "Import from Firefox…")), "Web picker did not connect controls and quota.");
                Capture(window, Path.Combine(directory, "windows-alibaba-web.png"));
            }
            finally { window.Close(); }
            File.WriteAllText(Path.Combine(directory, "windows-alibaba-cli-evidence.json"), JsonSerializer.Serialize(new
            {
                fixture = true, cli = "injected reader; no real CLI account", http = "in-memory only",
                explicitSourcesAndFourRegions = "PASS", autoFallback = "PASS", legacyWebPreserved = "PASS",
                staleResponseDiscard = "PASS", browserVerificationIsolation = "PASS", damagedOptionalWeb = "PASS", noCliCacheOrRetention = "PASS",
                actualStoreWpf = "PASS", cliCalls, httpCalls
            }, JsonOptions));
        }
        finally
        {
            foreach (var key in AlibabaEnvironmentKeys) Environment.SetEnvironmentVariable(key, environment[key]);
            foreach (var key in keys) { if (previous[key] is { } value) vault.Save(key, value); else vault.Delete(key); }
            Directory.Delete(fixtureDirectory, true); settings.Save(settings.Current with { EnabledProviders = enabled }); owner.Navigate("usage");
        }
    }
}
