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
                "moonshot" => "provider:moonshot:" + (MoonshotAuthentication.Region(vault.Load("setting:moonshot:MOONSHOT_REGION")) ?? "international"),
                "minimax" => "provider:minimax:" + (MiniMaxAuthentication.Region(vault.Load("setting:minimax:MINIMAX_REGION")) ?? "global"),
                _ => "provider:" + id
            };
            AddSecretField(key, id, id == "copilot" ? "Save access token" : "Save API key");
        }
        var fields = NativeProviders.Settings(id).Select(x => (x.Key, Title: x.Label, Type: x.Key.Contains("SECRET", StringComparison.Ordinal) || x.Key.EndsWith("_TOKEN", StringComparison.Ordinal) ? "secure" : "text"));
        if (ScriptProviders.Catalog.TryGetValue(id, out var script))
            fields = fields.Concat(script.Settings.Select(x => (x.Key, x.Title, x.Type)));
        foreach (var field in fields.DistinctBy(x => x.Key))
        {
            if (!OwnedProviderAccess.IsConnectionSetting(field.Key) || OwnedProviderAccess.CredentialKeys(id).Contains(field.Key)) continue;
            body.Children.Add(Ui.Text(field.Title, 12));
            string key = "setting:" + id + ":" + field.Key;
            if (field.Type == "secure") AddSecretField(key, id, "Save credential");
            else AddSettingField(key, id);
        }
    }
}
