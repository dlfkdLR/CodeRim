using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task CopilotAuthenticationRegression(AppSettings settings, CredentialVault vault, string directory)
    {
        var keys = new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_CONFIG_DIR" };
        var environment = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var root = Path.Combine(Path.GetTempPath(), "CodeRim-GitHub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); CredentialVault.RestrictDirectory(root);
        string? hostsToken = "hosts"; var cliToken = "cli"; var cliCalls = 0; var switchDuringFetch = false;
        var calls = new List<string?>();
        try
        {
            Environment.SetEnvironmentVariable("GH_CONFIG_DIR", root);
            File.WriteAllText(Path.Combine(root, "hosts.yml"), "github.com:\n  user: fixture-host-owner\n  oauth_token: hosts\n");
            Environment.SetEnvironmentVariable("GH_TOKEN", " "); Environment.SetEnvironmentVariable("GITHUB_TOKEN", "environment");
            using var connections = new ProviderConnections(vault,
                http: new HttpProviders(new AmpFixtureHandler(request =>
                {
                    Require(request.RequestUri!.AbsoluteUri == "https://api.github.com/copilot_internal/user"
                        && request.Headers.GetValues("X-GitHub-Api-Version").Single() == "2022-11-28", "Copilot endpoint/header contract drifted.");
                    calls.Add(request.Headers.Authorization!.Parameter);
                    if (switchDuringFetch) cliToken = "replacement";
                    return new(HttpStatusCode.OK) { Content = new StringContent("""{"quota_snapshots":{"premium_interactions":{"entitlement":100,"remaining":67}}}""") };
                })), nativeCredentialReader: id => id == "copilot" ? hostsToken : null,
                copilotCliReader: token => { token.ThrowIfCancellationRequested(); cliCalls++; return Task.FromResult<string?>(cliToken); });
            vault.Save("provider:copilot", "saved");
            var saved = await connections.FetchAsync("copilot", settings, CancellationToken.None);
            Require(saved.Headline?.UsedPercent == 33 && saved.Account is { Label: null, Source: "GitHub" }
                && calls[^1] == "saved" && cliCalls == 0, "Saved Copilot token lost precedence.");
            vault.Delete("provider:copilot");
            var ambient = await connections.FetchAsync("copilot", settings, CancellationToken.None);
            Require(ambient.Account is { Label: null, Source: "GitHub" } && calls[^1] == "environment" && cliCalls == 0, "Blank GH_TOKEN suppressed GITHUB_TOKEN.");
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            var hosts = await connections.FetchAsync("copilot", settings, CancellationToken.None);
            Require(hosts.Account is { Label: "fixture-host-owner", Source: "GitHub" } && calls[^1] == "hosts" && connections.CanCache("copilot") && cliCalls == 0, "GitHub CLI hosts token was not used.");
            hostsToken = null;
            Require(!connections.CanCache("copilot") && connections.Scope("copilot") is { Length: > 0 }, "CLI-only credentials were eligible for persisted quota.");
            var cli = await connections.FetchAsync("copilot", settings, CancellationToken.None);
            Require(cli.Account is { Label: null, Source: "GitHub" } && calls[^1] == "cli" && cliCalls == 2, "CLI credential was not rechecked before publishing.");
            switchDuringFetch = true;
            var changed = await connections.FetchAsync("copilot", settings, CancellationToken.None);
            Require(changed.State == ReadingState.Unavailable && changed.Windows.Count == 0 && changed.Account is null, "Copilot account replacement published stale quota.");
            File.WriteAllText(Path.Combine(directory, "windows-copilot-auth-evidence.json"), JsonSerializer.Serialize(new {
                fixture = true, network = "in-memory only", cli = "injected reader only", savedEnvironmentHostsCli = "PASS",
                endpointAndVersion = "PASS", cliNoPersistentCache = "PASS", accountReplacementInvalidation = "PASS", tokenBoundAccountLabel = "PASS", unrelatedEnvironmentKeyHasNoHostsLabel = "PASS"
            }, JsonOptions));
        }
        finally
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, environment[key]);
            vault.Delete("provider:copilot"); Directory.Delete(root, recursive: true);
        }
    }
}
