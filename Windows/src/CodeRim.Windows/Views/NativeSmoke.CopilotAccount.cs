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
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static readonly string[] CopilotAccountEnvironmentKeys = ["GH_CONFIG_DIR", "GH_TOKEN", "GITHUB_TOKEN"];
    private static async Task CopilotAccountSummaryRegression(AppSettingsStore settings, CredentialVault vault, string directory)
    {
        Require(Path.GetFileName(CompanionFile.DataDirectory).StartsWith("CodeRim-Smoke-", StringComparison.Ordinal), "Copilot account regression requires isolated storage.");
        var before = settings.Current;
        var snapshot = File.Exists(CompanionFile.SnapshotPath) ? File.ReadAllBytes(CompanionFile.SnapshotPath) : null;
        var environment = CopilotAccountEnvironmentKeys.ToDictionary(x => x, Environment.GetEnvironmentVariable);
        var secret = vault.Load("provider:copilot");
        var root = Path.Combine(CompanionFile.DataDirectory, "github-account-fixture-" + Guid.NewGuid().ToString("N"));
        var hosts = Path.Combine(root, "hosts.yml");
        DashboardStore? store = null; DashboardWindow? window = null;
        Exception? failure = null; var errors = new List<Exception>(); var checks = new List<string>();
        var requests = 0; var cliReads = 0; var timeout = false; var pause = false; var cliTokenAvailable = true;
        var responseStatus = HttpStatusCode.ServiceUnavailable;
        var sent = new List<string?>();
        TaskCompletionSource? entered = null; TaskCompletionSource? release = null;
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(hosts, "github.com:\n  user: fixture-user\n");
            Environment.SetEnvironmentVariable("GH_CONFIG_DIR", root);
            Environment.SetEnvironmentVariable("GH_TOKEN", null); Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            vault.Delete("provider:copilot"); settings.Save(before with { EnabledProviders = ["copilot"] });
            var connections = new ProviderConnections(vault, http: new HttpProviders(new FactoryFixtureHandler(async request =>
            {
                Require(request.RequestUri!.AbsoluteUri == "https://api.github.com/copilot_internal/user", "Copilot fixture escaped its origin.");
                requests++; sent.Add(request.Headers.Authorization?.Parameter);
                var status = responseStatus;
                if (pause) { pause = false; entered!.TrySetResult(); await release!.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                if (timeout) throw new OperationCanceledException("Synthetic Copilot timeout");
                return new(status) { Content = new StringContent("""{"quota_snapshots":{"premium_interactions":{"entitlement":100,"used":25}}}""") };
            })), nativeCredentialReader: _ => GitHubAuthentication.ParseHosts(CopilotConnection.ScopeMarker()),
                nativeSummaryReader: _ => NativeCredentials.ReadSummary("copilot"), copilotCliReader: _ =>
                { cliReads++; return Task.FromResult(cliTokenAvailable ? "os-store-fixture-token" : null); });
            var claude = new ClaudeIntegration(new(false, null), new ClaudeFixtureOperations(() => null), (_, _) => { });
            store = new DashboardStore(settings, vault, providerConnections: connections, claudeIntegration: claude);
            store.Readings.Remove("copilot"); await store.RefreshProviderAccountsAsync();
            window = new DashboardWindow(store, settings, vault); window.Show(); window.Navigate("copilot"); await Idle();
            Require(requests == 0 && cliReads == 0 && Identity().IsVisible && Label() == "fixture-user"
                && ProviderHeaderStatus(window) == "Not connected", "Username-only discovery sent a request/CLI command or lost the initial Account.");
            var draft = Descendants<PasswordBox>(window).First(); draft.Password = "unsaved-copilot-fixture";
            checks.Add("Actual isolated hosts reader displays username-only Account before HTTP or CLI execution");
            Capture(window, Path.Combine(directory, "windows-copilot-account-initial.png"));
            foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.Unauthorized })
            {
                responseStatus = status; await store.RefreshProviderAsync("copilot"); await Idle();
                RequireAbsentProviderPresentation(window, store.Readings["copilot"]);
                Require(Identity().IsVisible && Label() == "fixture-user" && store.Readings["copilot"].Windows.Count == 0,
                    "Copilot failure erased independently detected Account or retained quota.");
            }
            timeout = true; await store.RefreshProviderAsync("copilot"); timeout = false; await Idle();
            RequireAbsentProviderPresentation(window, store.Readings["copilot"]);
            Require(Label() == "fixture-user" && ReferenceEquals(draft, Descendants<PasswordBox>(window).First())
                && draft.Password == "unsaved-copilot-fixture", "Copilot failure erased the detected account or replaced the input draft.");
            checks.Add("First 503, 401 and timeout retain Account while quota and failure UI are absent");
            responseStatus = HttpStatusCode.OK; await store.RefreshProviderAsync("copilot"); await Idle();
            Require(store.Readings["copilot"].Windows.Single().UsedPercent == 25 && ProviderHeaderStatus(window) == "Connected",
                "Copilot fixture did not actually recover quota.");
            window.Navigate("providers"); await Idle(); var row = ProviderListStatus(window, "copilot");
            Require(row.Text.Contains("fixture-user", StringComparison.Ordinal) && row.Text.Contains('%'), "Copilot list omitted recovered Account/quota.");
            responseStatus = HttpStatusCode.ServiceUnavailable; await store.RefreshProviderAsync("copilot"); await Idle();
            Require(ReferenceEquals(row, ProviderListStatus(window, "copilot")) && row.Text.Contains("fixture-user", StringComparison.Ordinal), "Copilot failed list replaced its row or lost Account.");
            RequireFailedProviderListRow(window, store.Readings["copilot"]);
            checks.Add("Success recovers quota and subsequent failure updates the same Providers row while retaining Account");
            window.Navigate("copilot"); await Idle();
            draft = Descendants<PasswordBox>(window).First(); draft.Password = "unsaved-copilot-fixture";
            File.WriteAllText(hosts, "github.com:\n  user: replacement-user\n");
            await store.RefreshProviderAccountAsync("copilot"); await Idle();
            Require(Label() == "replacement-user" && store.AccountDisplay("copilot").Reading is null
                && ReferenceEquals(draft, Descendants<PasswordBox>(window).First()) && draft.Password == "unsaved-copilot-fixture",
                "Username-only rotation mixed quota or replaced the input draft.");
            checks.Add("Username-only account rotation invalidates quota without replacing connection inputs");

            Environment.SetEnvironmentVariable("GH_TOKEN", " "); Environment.SetEnvironmentVariable("GITHUB_TOKEN", "environment-fixture-token");
            await store.RefreshProviderAccountAsync("copilot"); var cliBefore = cliReads;
            Require(store.ProviderAccountDisplay("copilot").Account is { Label: null, Source: "GitHub" }, "Environment key borrowed local username.");
            await store.RefreshProviderAsync("copilot");
            Require(sent[^1] == "environment-fixture-token" && cliReads == cliBefore, "Display/request alias precedence differs or unnecessary gh execution occurred.");
            vault.Save("provider:copilot", "saved-fixture-token"); await store.RefreshProviderAccountAsync("copilot");
            await store.RefreshProviderAsync("copilot");
            Require(store.ProviderAccountDisplay("copilot").Account is { Label: null, Source: "GitHub" }
                && sent[^1] == "saved-fixture-token" && cliReads == cliBefore, "Saved key borrowed local username or lost request precedence.");
            checks.Add("Blank GH_TOKEN falls through to GITHUB_TOKEN; saved/environment keys never borrow CLI identity or run gh");
            vault.Delete("provider:copilot"); Environment.SetEnvironmentVariable("GH_TOKEN", null); Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            cliTokenAvailable = false; var requestsBefore = requests; await store.RefreshProviderAsync("copilot"); await Idle();
            Require(requests == requestsBefore && Identity().IsVisible && Label() == "replacement-user"
                && store.Readings["copilot"].State == ReadingState.NeedsAuth, "Display-only Account authorized HTTP without any request token.");
            checks.Add("Username-only display does not authorize HTTP when the CLI supplies no token");

            cliTokenAvailable = true;
            foreach (var inline in new[] { false, true })
            {
                var suffix = inline ? "  oauth_token: unchanged-inline-token\n" : "";
                File.WriteAllText(hosts, "github.com:\n  user: before-request\n" + suffix);
                await store.RefreshProviderAccountAsync("copilot");
                var scopeBefore = connections.Scope("copilot"); responseStatus = HttpStatusCode.OK;
                entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously); pause = true;
                var committed = new List<ProviderReading>();
                void Record(ProviderReading reading) { if (reading.Id == "copilot") committed.Add(reading); }
                store.ReadingUpdated += Record;
                var pending = store.RefreshProviderAsync("copilot"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                File.WriteAllText(hosts, "github.com:\n  user: rotated-during-request\n" + suffix);
                if (inline) Require(connections.Scope("copilot") == scopeBefore, "Inline rotation fixture did not retain the same request credential scope.");
                responseStatus = HttpStatusCode.ServiceUnavailable; release.TrySetResult(); await pending;
                await store.WaitForProviderIdleAsync("copilot"); await Idle(); store.ReadingUpdated -= Record;
                Require(Label() == "rotated-during-request" && committed.All(x => x.Windows.Count == 0)
                    && store.Readings.GetValueOrDefault("copilot")?.Windows.Count is null or 0,
                    "Old-owner success was published after username rotation with an unchanged credential.");
                checks.Add(inline ? "Independent Account version rejects in-flight old-owner success even when the inline credential scope is unchanged"
                    : "Username-only rotation rejects in-flight old-owner success even when the injected CLI token is unchanged");
            }
            File.WriteAllText(hosts, "github.com:\n  user: ambiguous\n  user: other\n");
            await store.RefreshProviderAccountAsync("copilot"); await Idle();
            Require(!Identity().IsVisible && store.AccountDisplay("copilot").Reading is null, "Malformed hosts retained account identity or quota.");
            checks.Add("Malformed host data removes display identity and its old quota");
            File.WriteAllText(Path.Combine(directory, "windows-copilot-account-summary.json"), JsonSerializer.Serialize(new
                { completed = true, fixture = true, realAccount = false, network = "in-memory only", cli = "injected only", checks }, JsonOptions));

            StackPanel Identity() => Descendants<StackPanel>(window).Single(x => AutomationProperties.GetAutomationId(x) == "provider.identity");
            string Label() => Descendants<TextBlock>(Identity()).Single(x => AutomationProperties.GetAutomationId(x) == "provider.identity.label").Text;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            release?.TrySetResult();
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { errors.Add(error); } }
            Restore(() => window?.Close()); Restore(() => store?.Dispose());
            if (store is not null)
                try { await store.WaitForAccountWorkIdleAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception error) when (error is not OutOfMemoryException) { errors.Add(error); }
            foreach (var pair in environment) Restore(() => Environment.SetEnvironmentVariable(pair.Key, pair.Value));
            Restore(() => { if (secret is null) vault.Delete("provider:copilot"); else vault.Save("provider:copilot", secret); });
            Restore(() => { if (snapshot is null) File.Delete(CompanionFile.SnapshotPath); else File.WriteAllBytes(CompanionFile.SnapshotPath, snapshot); });
            Restore(() => settings.Save(before)); Restore(() => Directory.Delete(root, true));
        }
        if (errors.Count > 0) throw new AggregateException("Copilot Account fixture cleanup failed.", failure is null ? errors : errors.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
