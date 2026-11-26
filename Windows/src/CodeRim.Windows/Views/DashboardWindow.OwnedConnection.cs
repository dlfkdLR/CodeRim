using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class DashboardWindow
{
    private void AddOwnedConnection(string id)
    {
        var plan = ConnectionPlan(id);
        body.Children.Add(Ui.Text(plan.Note, 12));
        if (id == "gemini")
        {
            body.Children.Add(Ui.Text("Google OAuth client ID", 12));
            AddSettingField("setting:gemini:ANTIGRAVITY_OAUTH_CLIENT_ID", id);
            body.Children.Add(Ui.Text("Google OAuth client secret", 12));
            AddSecretField("setting:gemini:ANTIGRAVITY_OAUTH_CLIENT_SECRET", id, "Save OAuth client secret");
            return;
        }
        if (!OwnedProviderAccess.HasKey(id) && !OwnedProviderAccess.HasLocalEndpoint(id)) return;
        if (OwnedProviderAccess.HasKey(id))
        {
            AddKeyLink(id);
            var key = id switch
            {
                "moonshot" => "provider:moonshot:" + (MoonshotAuthentication.Region(ProviderConnections.EffectiveSetting(vault, id, "MOONSHOT_REGION")) ?? "international"),
                "minimax" => "provider:minimax:" + (MiniMaxAuthentication.Region(ProviderConnections.EffectiveSetting(vault, id, "MINIMAX_REGION")) ?? "global"),
                _ => "provider:" + id
            };
            if (id == "moonshot") body.Children.Add(Ui.Text(key.EndsWith(":china", StringComparison.Ordinal) ? "Moonshot China API key" : "Moonshot International API key"));
            if (id == "ollama") AddOwnedOllamaKey(key);
            else AddSecretField(key, id, id == "copilot" ? "Save access token" : "Save API key");
        }
        var fields = NativeProviders.Settings(id).Select(x => (x.Key, Title: x.Label, Type: x.Key.Contains("SECRET", StringComparison.Ordinal) || x.Key.EndsWith("_TOKEN", StringComparison.Ordinal) ? "secure" : "text"));
        if (ScriptProviders.Catalog.TryGetValue(id, out var script))
            fields = fields.Concat(script.Settings.Select(x => (x.Key, x.Title, x.Type)));
        foreach (var field in fields.DistinctBy(x => x.Key))
        {
            if (!OwnedProviderAccess.IsConnectionSetting(field.Key) || OwnedProviderAccess.CredentialKeys(id).Contains(field.Key)) continue;
            string key = "setting:" + id + ":" + field.Key;
            if (field.Key is "MOONSHOT_REGION" or "MINIMAX_REGION")
            {
                var moonshot = id == "moonshot";
                var region = ProviderConnections.EffectiveSetting(vault, id, field.Key);
                var local = moonshot ? "International" : "Global";
                var selected = (moonshot ? MoonshotAuthentication.Region(region) == "china" : MiniMaxAuthentication.Region(region) == "cn")
                    ? "China" : local;
                body.Children.Add(SettingsUi.Picker("Region", new[] { local, "China" }, selected, value =>
                    SaveProviderSetting(id, key, value == "China" ? (moonshot ? "china" : "cn") : local.ToLowerInvariant(),
                        "Could not save the region.")));
                continue;
            }
            body.Children.Add(Ui.Text(field.Title, 12));
            if (field.Type == "secure") AddSecretField(key, id, "Save credential");
            else AddSettingField(key, id);
        }
    }

    private void AddOwnedOllamaKey(string key)
    {
        var draft = new PasswordBox { MaxLength = 32768, Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 8) };
        AutomationProperties.SetName(draft, "Ollama API key");
        var feedback = Ui.Text("", 11, "#B7B8BD");
        var save = Ui.Button("Save API key", () => { });
        var remove = Ui.Button("Remove saved value", () => { });
        bool hasSavedKey;
        try { hasSavedKey = vault.Version(key) is not null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            feedback.Text = "Could not read the saved key.";
            hasSavedKey = false;
        }
        void Update()
        {
            var label = hasSavedKey ? "Replace API key" : "Save API key";
            save.Content = label;
            AutomationProperties.SetName(save, label);
            save.IsEnabled = !string.IsNullOrWhiteSpace(draft.Password);
            remove.IsEnabled = hasSavedKey;
        }
        void Commit(bool delete)
        {
            if (!delete && string.IsNullOrWhiteSpace(draft.Password)) return;
            try
            {
                if (delete) vault.Delete(key);
                else vault.Save(key, draft.Password.Trim());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
            {
                feedback.Text = delete ? "Could not remove the saved key." : "Could not save the key.";
                return;
            }
            hasSavedKey = !delete;
            draft.Clear();
            Update();
            feedback.Text = delete ? "Removed." : "Saved.";
            store.InvalidateAccount("ollama");
            _ = store.RefreshProviderAsync("ollama");
        }
        AutomationProperties.SetAutomationId(draft, "ollama.key");
        AutomationProperties.SetAutomationId(save, "ollama.save");
        AutomationProperties.SetAutomationId(remove, "ollama.remove");
        AutomationProperties.SetAutomationId(feedback, "ollama.feedback");
        save.Click += (_, _) => Commit(false);
        remove.Click += (_, _) => Commit(true);
        draft.PasswordChanged += (_, _) => Update();
        draft.KeyDown += (_, e) => { if (e.Key == Key.Return) { e.Handled = true; Commit(false); } };
        Update();
        foreach (var element in new UIElement[] { draft, save, remove, feedback }) body.Children.Add(element);
    }
}
