using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static readonly string[] AuthViewEnvironmentKeys = ["GH_TOKEN", "GITHUB_TOKEN", "CODEBUFF_API_KEY", "MOONSHOT_REGION", "MOONSHOT_API_KEY",
        "MOONSHOT_KEY", "CODEXBAR_MOONSHOT_API_KEY", "CODEXBAR_MOONSHOT_API_KEY_REGION"];
    private static async Task AdditionalAuthenticationViews(AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var enabled = settings.Current.EnabledProviders;
        var keys = ScriptProviders.Catalog["glm"].Settings.Select(setting => setting.Key)
            .Concat(AuthViewEnvironmentKeys).Distinct().ToArray();
        var environment = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var evidence = new List<object>();
        try
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, null);
            foreach (var id in new[] { "copilot", "glm", "codebuff", "moonshot" })
            {
                var count = 0;
                HttpResponseMessage Response(HttpRequestMessage request)
                {
                    count++;
                    var json = id switch {
                        "copilot" => """{"quota_snapshots":{"premium_interactions":{"entitlement":100,"remaining":67}}}""",
                        "glm" => """{"success":true,"code":200,"data":{"limits":[{"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":33}]}}""",
                        "codebuff" => request.RequestUri!.AbsolutePath == "/api/v1/usage"
                            ? """{"usage":12,"quota":100}""" : """{"subscription":{"displayName":"Pro"}}""",
                        _ => """{"code":0,"status":true,"scode":"ok","data":{"available_balance":12.5,"voucher_balance":2.5,"cash_balance":10}}"""
                    };
                    return new(HttpStatusCode.OK) { Content = new StringContent(json) };
                }
                string? Local(string provider) => provider switch {
                    "copilot" => "synthetic-github",
                    "codebuff" => "synthetic-codebuff",
                    "glm" => GlmAuthentication.Serialize(new GlmCredential("synthetic-glm", "bigmodel-cn", "OpenCode")),
                    _ => null
                };
                if (id == "moonshot")
                {
                    vault.Save("setting:moonshot:MOONSHOT_REGION", "international");
                    vault.Save("provider:moonshot:international", "synthetic-moonshot");
                }
                settings.Save(settings.Current with { EnabledProviders = [id] });
                using var store = new DashboardStore(settings, vault, providerConnections: new ProviderConnections(vault,
                    native: new NativeProviders(new AmpFixtureHandler(Response)),
                    http: new HttpProviders(new AmpFixtureHandler(Response)), nativeCredentialReader: Local,
                    scripts: new ScriptProviders(new AmpFixtureHandler(Response)),
                    copilotCliReader: _ => throw new InvalidOperationException("Synthetic hosts credentials unexpectedly started gh.")));
                var window = new DashboardWindow(store, settings, vault); window.Show(); window.Navigate(id);
                try
                {
                    await store.RefreshProviderAsync(id); await Idle();
                    var reading = store.Readings[id];
                    Require(!store.Synthetic && count > 0 && reading.State == ReadingState.Ready,
                        "Native auth view did not use its real connector and store.");
                    var texts = Descendants<TextBlock>(window).Select(text => text.Text).ToArray();
                    if (id == "moonshot")
                    {
                        Require(reading.Headline is { Unit: "USD", UsedPercent: null }
                            && texts.Any(text => text.Contains("12", StringComparison.Ordinal) && text.Contains("USD", StringComparison.Ordinal)),
                            "Moonshot balance was replaced with synthetic percentage quota.");
                    }
                    else Require(Descendants<ProgressBar>(window).Any(bar => Math.Abs(bar.Value - (id == "codebuff" ? 12 : 33)) < 0.001),
                        "Provider quota did not reach its native view.");
                    if (id is "copilot" or "glm")
                    {
                        var source = id == "copilot" ? "GitHub" : "OpenCode";
                        Require(reading.Account is { Label: null } && reading.Account.Source == source
                            && Descendants<TextBlock>(window).Any(text => System.Windows.Automation.AutomationProperties.GetAutomationId(text) == "provider.identity.source" && text.Text == source),
                            "Provider transport account source did not reach the mounted detail view.");
                        if (id == "glm") Require(Descendants<TextBlock>(window).SelectMany(text => text.Inlines.OfType<System.Windows.Documents.Hyperlink>())
                            .Any(link => System.Windows.Automation.AutomationProperties.GetName(link) == "Open usage page" && link.NavigateUri.AbsoluteUri == "https://open.bigmodel.cn/usage"),
                            "GLM native account view uses another region's management page.");
                    }
                    Capture(window, Path.Combine(directory, "windows-auth-view-" + id + ".png"));
                    evidence.Add(new { provider = id, requests = count, reading.State, reading.Plan,
                        reading.Windows, connectorStoreWpf = "PASS", syntheticPreview = false });
                }
                finally { window.Close(); }
            }
            File.WriteAllText(Path.Combine(directory, "windows-auth-views-evidence.json"), JsonSerializer.Serialize(new {
                fixture = true, credentials = "isolated injected local reader and DPAPI fixtures",
                network = "in-memory only", providers = evidence
            }, JsonOptions));
        }
        finally
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, environment[key]);
            vault.Delete("setting:moonshot:MOONSHOT_REGION"); vault.Delete("provider:moonshot:international");
            settings.Save(settings.Current with { EnabledProviders = enabled });
        }
    }
}
