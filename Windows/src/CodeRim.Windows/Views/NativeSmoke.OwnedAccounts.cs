using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Button = System.Windows.Controls.Button;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    // Invoked in a separate native QA process. All requests run through injected
    // handlers and the real Windows vault is confined to its new fixture directory.
    internal static async Task<string> OwnedAccountsAsync()
    {
        if (!OwnedProviderAccess.Enabled) throw new InvalidOperationException("Requires a migration build.");
        string root = Path.Combine(Path.GetTempPath(), "CodeRim-Owned-Account-Fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODERIM_DATA_DIR", root);
        var keys = ProviderCatalog.All.SelectMany(p => p.EnvironmentKeys.Concat(OwnedProviderAccess.CredentialKeys(p.Id)))
            .Concat(["GH_TOKEN", "GITHUB_TOKEN", "CODEX_HOME", "CLAUDE_CONFIG_DIR", "GH_CONFIG_DIR", "AWS_CONFIG_FILE", "AWS_SHARED_CREDENTIALS_FILE"]);
        foreach (var key in keys.Distinct(StringComparer.Ordinal)) Environment.SetEnvironmentVariable(key, null);
        foreach (var key in new[] { "CODEX_HOME", "CLAUDE_CONFIG_DIR", "GH_CONFIG_DIR", "AWS_CONFIG_FILE", "AWS_SHARED_CREDENTIALS_FILE" })
            Environment.SetEnvironmentVariable(key, Path.Combine(root, "unread-foreign-" + key));
        try
        {
            var vault = new CredentialVault();
            var settings = new AppSettingsStore().Current;
            // Historical entries have no trustworthy sign-in origin. Preserve the label and bytes.
            var historical = """[{"Identity":{"Id":"old","Email":"old@example.invalid"},"Credential":"unused-fixture"}]""";
            vault.Save("accounts:codex", historical);
            var savedAccounts = new SavedAccounts(vault);
            if (savedAccounts.Read("codex").Count != 0 || savedAccounts.SelectedForCodeRim("codex") is not null
                || !savedAccounts.PreviousAccountLabels("codex").SequenceEqual(["old@example.invalid"])
                || vault.Load("accounts:codex") != historical)
                throw new InvalidDataException("Historical account provenance was lost.");
            Environment.SetEnvironmentVariable("GH_TOKEN", "unrelated-ci-fixture");
            Environment.SetEnvironmentVariable("ANTIGRAVITY_OAUTH_CREDENTIALS_JSON", "unrelated-cli-fixture");
            var requests = new List<string>();
            const string sample = "gho_synthetic_owned_fixture";
            Task<HttpResponseMessage> Reply(HttpRequestMessage request)
            {
                var endpoint = request.RequestUri?.AbsoluteUri ?? "";
                requests.Add(endpoint);
                string json = endpoint switch
                {
                    "https://github.com/login/device/code" => """{"device_code":"fixture-device","user_code":"TEST-CODE","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""",
                    "https://github.com/login/oauth/access_token" => """{"access_token":"gho_synthetic_owned_fixture"}""",
                    "https://api.github.com/copilot_internal/user" when request.Headers.Authorization?.Parameter == sample => """{"quota_snapshots":{"chat":{"remaining":300,"entitlement":400}}}""",
                    "https://api.deepseek.com/user/balance" when request.Headers.Authorization?.Parameter == "synthetic-api-key" => """{"is_available":true,"balance_infos":[{"currency":"USD","total_balance":"3","granted_balance":"0","topped_up_balance":"3"}]}""",
                    _ => throw new InvalidOperationException("Unexpected fixture transport.")
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            using var connections = new ProviderConnections(vault, new NativeProviders(new FactoryFixtureHandler(Reply)),
                new HttpProviders(new FactoryFixtureHandler(Reply)), _ => throw new InvalidOperationException("External reader invoked."),
                new ScriptProviders(new FactoryFixtureHandler(Reply)), _ => throw new InvalidOperationException("External CLI invoked."),
                (_, _, _) => throw new InvalidOperationException("External CLI invoked."),
                _ => throw new InvalidOperationException("External account invoked."), _ => throw new InvalidOperationException("External summary invoked."));
            foreach (var provider in ProviderCatalog.All)
            {
                _ = connections.Scope(provider.Id);
                _ = connections.CanCache(provider.Id);
                _ = connections.ReadAccountSummary(provider.Id);
                if (provider.Id == "claude" || OwnedProviderAccess.HasLocalEndpoint(provider.Id)) continue;
                var empty = await connections.FetchAsync(provider.Id, settings, CancellationToken.None);
                if (empty.State is not (ReadingState.NeedsAuth or ReadingState.Unsupported))
                    throw new InvalidDataException("Wrong unconnected state for " + provider.Id);
            }
            if (requests.Count != 0) throw new InvalidOperationException("An unconnected provider sent a request.");
            using var client = new HttpClient(new FactoryFixtureHandler(Reply));
            string? shown = null;
            var signed = await GitHubDeviceFlow.RunAsync(client, _ => { }, uri => shown = uri.AbsoluteUri, _ => { },
                value => vault.Save("provider:copilot", value), CancellationToken.None, (_, _) => Task.CompletedTask);
            if (!signed.SignedIn || shown != "https://github.com/login/device" || vault.Load("provider:copilot") != sample)
                throw new InvalidDataException("The official device response was not linked to the fixture vault.");
            if (new CredentialVault().Load("provider:copilot") != sample)
                throw new InvalidDataException("The device authorization could not be reopened from the synthetic CodeRim vault.");
            var a = connections.Scope("copilot");
            var usage = await connections.FetchAsync("copilot", settings, CancellationToken.None);
            if (a is null || usage.State != ReadingState.Ready) throw new InvalidDataException("Unable to read the fixture's linked GitHub account.");
            vault.Delete("provider:copilot");
            if (connections.Scope("copilot") is not null || (await connections.FetchAsync("copilot", settings, CancellationToken.None)).State != ReadingState.NeedsAuth)
                throw new InvalidDataException("A removed account fell back to another source.");
            vault.Save("provider:deepseek", "synthetic-api-key");
            var balance = await connections.FetchAsync("deepseek", settings, CancellationToken.None);
            if (balance.State != ReadingState.Ready || requests.Count != 4) throw new InvalidDataException("Explicit API key did not reach the fixed provider endpoint.");
            return JsonSerializer.Serialize(new { providers = ProviderCatalog.All.Count,
                historicalLabelPreserved = true, missingSentRequests = 0, officialDevice = signed.SignedIn, vaultRestored = true, apiKey = true, httpRequests = requests.Count });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // The packaged smoke opens the production Settings page within its synthetic application profile.
    private static async Task OwnedConnectionViewsRegression(DashboardWindow dashboard, CredentialVault vault, string directory, string[] inspected)
    {
        foreach (var id in inspected)
        {
            dashboard.Navigate(id); await Idle();
            Require(Descendants<TextBlock>(dashboard).Any(text => text.Text == ProviderCatalog.Find(id)!.Name)
                && Descendants<Button>(dashboard).Any(button => AutomationProperties.GetAutomationId(button) == "provider.refresh"),
                "The provider Settings page did not open for " + id + ".");
            Require(!Descendants<Button>(dashboard).Any(button => button.Content is string title &&
                    (title.Contains("Import", StringComparison.OrdinalIgnoreCase) || title.Contains("browser profile", StringComparison.OrdinalIgnoreCase))),
                "An external credential action appeared in " + id + " Settings.");
        }
        dashboard.Navigate("ollama"); await Idle();
        T Control<T>(string id) where T : UIElement =>
            Descendants<T>(dashboard).SingleOrDefault(control => AutomationProperties.GetAutomationId(control) == "ollama." + id)
            ?? throw new InvalidDataException("Missing Ollama Settings control: " + id);
        var draft = Control<PasswordBox>("key");
        var save = Control<Button>("save");
        var remove = Control<Button>("remove");
        var feedback = Control<TextBlock>("feedback");
        const string slot = "provider:ollama";
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(vault.Load(slot) is null && !save.IsEnabled && !remove.IsEnabled, "Unexpected initial key controls.");
        draft.Password = " synthetic-key-one "; Click(save);
        Require(vault.Load(slot) == "synthetic-key-one" && !save.IsEnabled && remove.IsEnabled
            && Equals(save.Content, "Replace API key") && feedback.Text == "Saved.", "Saved key and displayed state disagree.");
        draft.Password = " 	  ";
        Click(save);
        Require(!save.IsEnabled && vault.Load(slot) == "synthetic-key-one", "A whitespace replacement changed the key.");
        draft.Password = "synthetic-key-two";
        var prior = vault.Version(slot);
        var entry = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(slot))) + ".bin";
        var encrypted = Path.Combine(CompanionFile.DataDirectory, "vault", entry);
        using (File.Open(encrypted, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Click(save);
            Require(draft.Password == "synthetic-key-two" && feedback.Text == "Could not save the key." && vault.Version(slot) == prior,
                "A rejected replacement changed the draft, key or success feedback.");
            Click(remove);
            Require(feedback.Text == "Could not remove the saved key." && remove.IsEnabled && vault.Version(slot) == prior,
                "A rejected removal hid the saved key.");
        }
        Click(save);
        Require(new CredentialVault().Load(slot) == "synthetic-key-two" && feedback.Text == "Saved.", "Key replacement did not persist.");
        Click(remove);
        Require(new CredentialVault().Load(slot) is null && !remove.IsEnabled && Equals(save.Content, "Save API key")
            && feedback.Text == "Removed.", "Key removal did not persist.");
        File.WriteAllText(Path.Combine(directory, "windows-owned-connection-settings.json"), JsonSerializer.Serialize(new
        {
            blankRejected = true, previousKeyPreserved = true, failedReplacementPreservesDraft = true,
            failedRemovalPreservesState = true, replacedKeyReopened = true, removedKeyReopened = true
        }));
    }
}
