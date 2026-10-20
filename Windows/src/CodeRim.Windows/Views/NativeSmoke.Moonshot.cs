using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task MoonshotRegionRegression(DashboardWindow dashboard, AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var providers = settings.Current.EnabledProviders;
        var keys = new[] { "MOONSHOT_REGION", "MOONSHOT_API_KEY", "MOONSHOT_KEY", "CODEXBAR_MOONSHOT_API_KEY", "CODEXBAR_MOONSHOT_API_KEY_REGION" };
        var environment = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var calls = new List<(string Host, string? Key)>();
        try
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, null);
            using var connections = new ProviderConnections(vault, http: new HttpProviders(new AmpFixtureHandler(request =>
            {
                calls.Add((request.RequestUri!.Host, request.Headers.Authorization?.Parameter));
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"code":0,"status":true,"scode":"ok","data":{"available_balance":12.5,"voucher_balance":2.5,"cash_balance":10}}""") };
            })));
            vault.Save("provider:moonshot", "legacy-international");
            vault.Save("setting:moonshot:MOONSHOT_REGION", "china");
            Require((await connections.FetchAsync("moonshot", settings.Current, CancellationToken.None)).State == ReadingState.NeedsAuth
                && calls.Count == 0, "China used the legacy international key.");
            vault.Save("provider:moonshot:china", "saved-china");
            var chinaScope = connections.Scope("moonshot");
            Require((await connections.FetchAsync("moonshot", settings.Current, CancellationToken.None)).State == ReadingState.Ready
                && calls[^1] == ("api.moonshot.cn", "saved-china"), "Moonshot China did not use its pinned origin/key.");
            vault.Save("provider:moonshot:china", "replaced-china");
            Require(connections.Scope("moonshot") != chinaScope, "Replacing the region key did not invalidate quota.");
            vault.Save("setting:moonshot:MOONSHOT_REGION", "international");
            Require((await connections.FetchAsync("moonshot", settings.Current, CancellationToken.None)).State == ReadingState.Ready
                && calls[^1] == ("api.moonshot.ai", "legacy-international"), "Legacy international key was not preserved.");
            vault.Delete("provider:moonshot"); Environment.SetEnvironmentVariable("MOONSHOT_REGION", "china");
            Environment.SetEnvironmentVariable("MOONSHOT_KEY", "environment-china");
            var before = calls.Count;
            Require((await connections.FetchAsync("moonshot", settings.Current, CancellationToken.None)).State == ReadingState.NeedsAuth
                && calls.Count == before, "Environment China key crossed the saved international selection.");
            vault.Save("setting:moonshot:MOONSHOT_REGION", " "); vault.Delete("provider:moonshot:china");
            Require((await connections.FetchAsync("moonshot", settings.Current, CancellationToken.None)).State == ReadingState.Ready
                && calls[^1] == ("api.moonshot.cn", "environment-china"), "Blank saved region did not follow the environment region/key alias.");
            settings.Save(settings.Current with { EnabledProviders = [..providers, "moonshot"] });
            dashboard.Navigate("moonshot"); await Idle();
            Require(Descendants<TextBlock>(dashboard).Any(text => text.Text == "Moonshot China API key"), "China region label is missing.");
            var picker = Descendants<ComboBox>(dashboard).Single(combo => AutomationProperties.GetName(combo) == "Region");
            picker.SelectedItem = "International"; await Idle();
            Require(vault.Load("setting:moonshot:MOONSHOT_REGION") == "international"
                && Descendants<TextBlock>(dashboard).Any(text => text.Text == "Moonshot International API key"), "Region selection did not update the credential field.");
            Capture(dashboard, Path.Combine(directory, "windows-moonshot-region.png"));
            File.WriteAllText(Path.Combine(directory, "windows-moonshot-region-evidence.json"), JsonSerializer.Serialize(new {
                fixture = true, networkRequests = "in-memory only", requestCount = calls.Count,
                regionKeyIsolation = "PASS", legacyInternational = "PASS", environmentAlias = "PASS", scopeAndUi = "PASS"
            }, JsonOptions));
        }
        finally
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, environment[key]);
            foreach (var key in new[] { "provider:moonshot", "provider:moonshot:china", "provider:moonshot:international", "setting:moonshot:MOONSHOT_REGION" }) vault.Delete(key);
            settings.Save(settings.Current with { EnabledProviders = providers }); dashboard.Navigate("usage");
        }
    }
}
