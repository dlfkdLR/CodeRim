using System.IO;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class DashboardWindow
{
    private void AddChromiumConnection(string id)
    {
        if (!ProviderConnections.ChromiumEnabled(vault, id) || id == "minimax" && MiniMaxAuthentication.Region(ProviderConnections.EffectiveSetting(vault, id, "MINIMAX_REGION")) is null) return;
        if (vault.Version(ProviderConnections.ChromiumStorageKey(vault, id)) is not null)
            body.Children.Add(Ui.Text("A Chromium sign-in is saved for this region. Removing it restores your other saved connections.", 12, "#A6A6AA"));
        body.Children.Add(Ui.Button("Import from Chromium profile…", () => ChromiumConnections.Import(this, id, vault,
            () => { store.InvalidateAccount(id); _ = store.RefreshProviderAsync(id); })));
        body.Children.Add(Ui.Button("Remove Chromium sign-in", () =>
        {
            try { vault.Delete(ProviderConnections.ChromiumStorageKey(vault, id)); store.InvalidateAccount(id); _ = store.RefreshProviderAsync(id); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { System.Windows.MessageBox.Show(this, "Could not remove the imported sign-in.", "CodeRim"); }
        }));
    }
    private void ClearChromiumForBrowser(string id)
    {
        if (id is "factory" or "minimax") vault.Delete(ProviderConnections.ChromiumStorageKey(vault, id));
    }
    private void ClearChromiumForManual(string id, string key)
    {
        if (id == "factory" && key is "provider:factory" or "cookie:factory" || id == "deepseek" && key == "provider:deepseek:web"
            || id == "minimax" && key == "cookie:minimax:" + ProviderConnections.ChromiumRegion(vault, id))
            vault.Delete(ProviderConnections.ChromiumStorageKey(vault, id));
    }
}
