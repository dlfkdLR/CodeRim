using System.IO;
using System.Text.Json;
using System.Windows;
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
        var previousClaude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var home = Path.Combine(Path.GetTempPath(), "coderim-account-display-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(home, "auth.json"); DashboardStore? store = null; Window? popupWindow = null; NotchWindow? accountNotch = null;
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
            // ScrollViewer materializes its content only after its control template is
            // loaded. Checking an unattached popup made every negative check vacuous.
            popupWindow = new Window { Width = 320, Height = 300, Content = popup, ShowInTaskbar = false };
            popupWindow.Show(); await Idle();
            Require(!Descendants<TextBlock>(popupWindow).Any(x => x.Text.Contains("Plan A", StringComparison.Ordinal) || x.Text.Contains("85%", StringComparison.Ordinal)), "Notch popup retained another account's quota.");
            Require(Descendants<TextBlock>(popupWindow).Any(x => x.Text.Contains("Pro 20x", StringComparison.Ordinal)), "Notch popup lost the current plan.");
            Capture(popupWindow, Path.Combine(directory, "windows-account-reading-ownership.png"));

            // Capture B's scope with remote limits disabled, then supply fixture data.
            await store.RefreshProviderAsync("codex");
            var now = DateTimeOffset.Now;
            store.Readings["codex"] = new("codex", ReadingState.Ready,
                [new("session", "5 hours", 95, now.AddMinutes(-1), 300), new("weekly", "Weekly", 40, now.AddDays(3), 10080)], now, Plan: "Cached plan is not authoritative");
            settings.Save(settings.Current with { AccountLimitsEnabled = true, Visibility = NotchVisibility.AlwaysShow, ReduceMotion = true });
            accountNotch = new NotchWindow(store, settings, _ => { }); accountNotch.Show(); await Idle();
            var ring = Descendants<ProviderRing>(accountNotch).Single(x => x.ProviderId == "codex");
            Require(store.AccountDisplay("codex").RawPlan == "pro" && ring.Reading is { State: ReadingState.Ready, Windows.Count: 1, Headline.Id: "weekly" },
                "Initial Pro ring retained the hidden five-hour quota or its stale reset.");
            Require(ProviderDisplayPolicy.Apply(store.AccountDisplay("codex").Reading, settings.Current)?.Windows.Count == 2,
                "Notch-only plan filtering removed the Usage quota.");
            accountNotch.OpenProvider("codex"); await Idle();
            Require(!Descendants<TextBlock>(accountNotch.PopupContent!).Any(x => x.Text == "5 hours")
                && Descendants<TextBlock>(accountNotch.PopupContent!).Any(x => x.Text == "Weekly"), "Mounted Pro popup did not hide only the five-hour window.");
            Capture(accountNotch.PopupContent!, Path.Combine(directory, "windows-codex-pro-quotas.png"));
            GuardedFile.Replace(path, Credential("account-b"), Credential("account-b", "prolite")); row.Refresh();
            Require(store.AccountDisplay("codex").Plan == "Pro 5x"
                && Descendants<TextBlock>(row).Any(x => x.Text.Contains("Pro 5x", StringComparison.Ordinal)), "Pro 5x was not mapped from current login metadata.");
            // A normal store notification must update the existing ring without rebuilding it.
            store.UpdateSessionActivity([new("plan-fixture", "codex", "Plan changed", "idle", now)]); await Idle();
            Require(ring.Reading is { Windows.Count: 2, Headline.Id: "session" }, "Live ring update hid the real Pro 5x five-hour quota.");
            accountNotch.OpenProvider("codex"); await Idle();
            Require(Descendants<TextBlock>(accountNotch.PopupContent!).Any(x => x.Text == "5 hours"), "Pro 5x popup lost the five-hour quota.");
            Capture(accountNotch.PopupContent!, Path.Combine(directory, "windows-codex-prolite-quotas.png"));
            GuardedFile.Replace(path, Credential("account-b", "prolite"), Credential("account-b"));
            store.Readings["codex"] = store.Readings["codex"] with { Windows = [new("session", "5 hours", 0, now.AddHours(2), 300)] };
            store.UpdateSessionActivity([]); await Idle();
            Require(ring.Reading is { Windows.Count: 1, Headline.Id: "session" }, "Pro fallback blanked its only reported quota.");
            accountNotch.Close(); accountNotch = null;
            File.Delete(path);
            Require(store.AccountDisplay("codex") is { Label: null, Plan: null, Reading: null }, "Sign-out kept private account data visible.");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", home);
            GuardedFile.WritePrivate(Path.Combine(home, ".credentials.json"), """{"claudeAiOauth":{"accessToken":"synthetic-access","refreshToken":"synthetic-refresh","subscriptionType":"max","expiresAt":4102444800000,"scopes":["user:inference"]}}""");
            var profilePath = Path.Combine(home, ".claude.json");
            foreach (var multiple in new[] { 5, 20 })
            {
                var profile = JsonSerializer.Serialize(new { oauthAccount = new { emailAddress = "max@example.invalid", accountUuid = "fixture-user",
                    organizationUuid = "fixture-org", userRateLimitTier = "default_claude_max_" + multiple + "x" } });
                if (File.Exists(profilePath)) GuardedFile.Replace(profilePath, GuardedFile.Read(profilePath), profile);
                else GuardedFile.WritePrivate(profilePath, profile);
                Require(store.AccountDisplay("claude").Plan == "Max " + multiple + "x" && SavedAccounts.Current("claude").Identity.Plan == "max",
                    "Claude display tier is missing or changed stored login identity metadata.");
                popupWindow.Content = NotchPopover.Create("claude", store, settings.Current, _ => { }); await Idle();
                Require(Descendants<TextBlock>(popupWindow).Any(x => x.Text == "Max " + multiple + "x"), "Claude tier did not reach the mounted popup.");
                Capture(popupWindow, Path.Combine(directory, "windows-claude-plan-" + multiple + "x.png"));
            }
            File.WriteAllText(Path.Combine(directory, "windows-account-reading-ownership.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Real local auth identity matches captured reading scope", "External same-workspace switch hides old plan and limits before provider polling", "Provider row and notch popup never pair new email with old quota", "Pro hides the five-hour notch quota using raw login metadata; Pro 5x keeps it on a live ring update", "Only-quota fallback and Usage data are retained", "Sign-out hides account data", "Matching Claude Max 5x/20x profile tier reaches popup without changing stored identity" } }));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => accountNotch?.Close()); Restore(() => popupWindow?.Close()); Restore(() => store?.Dispose()); Restore(() => Environment.SetEnvironmentVariable("CODEX_HOME", previousHome));
            Restore(() => Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousClaude));
            Restore(() => settings.Save(before)); Restore(() => Directory.Delete(home, true));
        }
        if (cleanup.Count > 0) throw new AggregateException("Account display fixture cleanup failed", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
