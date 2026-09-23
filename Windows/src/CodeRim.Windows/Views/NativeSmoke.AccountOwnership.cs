using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task ProviderAccountOwnershipRegression(AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var before = settings.Current; var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var home = Path.Combine(Path.GetTempPath(), "coderim-account-display-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(home, "auth.json"); DashboardStore? store = null;
        Exception? failure = null; var cleanup = new List<Exception>();
        string Credential(string subject, string? plan = null)
        {
            var claims = new Dictionary<string, object> { ["email"] = subject + "@example.invalid", ["sub"] = subject,
                ["https://api.openai.com/auth"] = new { chatgpt_account_id = "fixture-workspace", chatgpt_plan_type = plan ?? (subject == "account-a" ? "plus" : "pro") } };
            var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return JsonSerializer.Serialize(new { tokens = new { access_token = "synthetic-access", refresh_token = "synthetic-refresh", id_token = "synthetic." + payload + ".signature", account_id = "fixture-workspace" } });
        }
        try
        {
            Directory.CreateDirectory(home); CredentialVault.RestrictDirectory(home); Environment.SetEnvironmentVariable("CODEX_HOME", home);
            var first = Credential("account-a"); GuardedFile.WritePrivate(path, first);
            settings.Save(before with { AccountLimitsEnabled = false, EnabledProviders = ["codex"] });
            // Disabled limits capture the real local ownership key without starting a CLI or network request.
            store = new DashboardStore(settings, vault, providerConnections: new ProviderConnections(vault));
            await store.RefreshProviderAsync("codex");
            store.Readings["codex"] = new("codex", ReadingState.Ready, [new("weekly", "Weekly", 85)], DateTimeOffset.Now, Plan: "Plan A");
            var row = new ProviderAccountRow("codex", store, settings, () => { }, () => { }, _ => { });
            Require(store.AccountDisplay("codex") is { Label: "account-a@example.invalid", Plan: "Plus", Reading.Plan: "Plan A" }, "Initial account display did not capture login plan and quota ownership.");
            GuardedFile.Replace(path, first, Credential("account-b")); row.Refresh();
            var display = store.AccountDisplay("codex");
            Require(display.Label == "account-b@example.invalid" && display.Plan == "Pro 20x" && display.Reading is null, "External CLI switch paired account B with account A's plan or quota.");
            var text = string.Join("|", Descendants<TextBlock>(row).Select(x => x.Text));
            Require(text.Contains("account-b@example.invalid", StringComparison.Ordinal) && text.Contains("Pro 20x", StringComparison.Ordinal)
                && !text.Contains("Plan A", StringComparison.Ordinal) && !text.Contains("85%", StringComparison.Ordinal), "Provider row rendered mixed account data or lost the current plan.");
            var popup = NotchPopover.Create("codex", store, settings.Current, _ => { });
            Require(!Descendants<TextBlock>(popup).Any(x => x.Text.Contains("Plan A", StringComparison.Ordinal) || x.Text.Contains("85%", StringComparison.Ordinal)), "Notch popup retained another account's quota.");
            Require(Descendants<TextBlock>(popup).Any(x => x.Text.Contains("Pro 20x", StringComparison.Ordinal)), "Notch popup lost the current plan.");
            GuardedFile.Replace(path, Credential("account-b"), Credential("account-b", "prolite")); row.Refresh();
            Require(store.AccountDisplay("codex").Plan == "Pro 5x"
                && Descendants<TextBlock>(row).Any(x => x.Text.Contains("Pro 5x", StringComparison.Ordinal)), "Pro 5x was not mapped from current login metadata.");
            File.Delete(path);
            Require(store.AccountDisplay("codex") is { Label: null, Plan: null, Reading: null }, "Sign-out kept private account data visible.");
            File.WriteAllText(Path.Combine(directory, "windows-account-reading-ownership.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Real local auth identity matches captured reading scope", "External same-workspace switch hides old plan and limits before provider polling", "Provider row and notch popup never pair new email with old quota", "Sign-out hides account data" } }));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => store?.Dispose()); Restore(() => Environment.SetEnvironmentVariable("CODEX_HOME", previousHome));
            Restore(() => settings.Save(before)); Restore(() => Directory.Delete(home, true));
        }
        if (cleanup.Count > 0) throw new AggregateException("Account display fixture cleanup failed", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
