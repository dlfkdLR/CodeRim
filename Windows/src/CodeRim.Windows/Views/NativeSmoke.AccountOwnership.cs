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
            var switchedSnapshot = store.CreateCompanionSnapshot(DateTimeOffset.Now).Providers.Single();
            Require(switchedSnapshot.Limits.Windows.Count == 0 && switchedSnapshot.RestartLimits.Windows.Single().UsedPercent == 85,
                "Companion mixed the new login with old quotas or discarded the owner-scoped restart cache.");
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
            var companionPath = Path.Combine(home, "companion.json");
            CompanionFile.Write(store.CreateCompanionSnapshot(now), companionPath);
            var published = CompanionFile.Read(companionPath).Providers.Single();
            Require(published.Limits is { State: ReadingState.Ready, Windows.Count: 1, Headline.Id: "weekly" }
                && published.RestartLimits.Windows.Count == 2, "Published Pro quota or retained restart data is incorrect.");
            var beforeCompanionPreferences = settings.Current; var beforeCompanionReading = store.Readings["codex"];
            var savedCompanion = File.Exists(CompanionFile.SnapshotPath) ? File.ReadAllBytes(CompanionFile.SnapshotPath) : null;
            Exception? restartFailure = null; var restartCleanup = new List<Exception>();
            try
            {
                settings.Save(settings.Current with { AdditionalLimitsEnabled = false, ResetCreditsEnabled = false });
                store.Readings["codex"] = beforeCompanionReading with { Windows = [.. beforeCompanionReading.Windows,
                    new("codex_bengalfox.secondary", "Weekly", 12, now.AddDays(3), 10080)] };
                Require(store.CreateCompanionSnapshot(now).Providers.Single().Limits.Windows.Any(window => window.Id == "codex_bengalfox.secondary"),
                    "Usage-only additional-window preference hid companion quotas.");
                store.Readings["codex"] = beforeCompanionReading; settings.Save(beforeCompanionPreferences);
                CompanionFile.Write(store.CreateCompanionSnapshot(now));
                using (var restarted = new DashboardStore(settings, vault, providerConnections: new ProviderConnections(vault)))
                    Require(restarted.Readings.GetValueOrDefault("codex") is { State: ReadingState.Stale, Windows.Count: 2 },
                        "Restart restored filtered Pro quotas instead of the full owned cache.");
                GuardedFile.Replace(path, Credential("account-b"), Credential("account-a"));
                using (var otherAccount = new DashboardStore(settings, vault, providerConnections: new ProviderConnections(vault)))
                    Require(!otherAccount.Readings.ContainsKey("codex"), "Restart restored another account's cached limits.");
            }
            catch (Exception error) { restartFailure = error; }
            finally
            {
                try { store.Readings["codex"] = beforeCompanionReading; settings.Save(beforeCompanionPreferences); }
                catch (Exception error) { restartCleanup.Add(error); }
                try { GuardedFile.Replace(path, GuardedFile.Read(path), Credential("account-b")); }
                catch (Exception error) { restartCleanup.Add(error); }
                try
                {
                    if (savedCompanion is null) File.Delete(CompanionFile.SnapshotPath);
                    else File.WriteAllBytes(CompanionFile.SnapshotPath, savedCompanion);
                }
                catch (Exception error) { restartCleanup.Add(error); }
            }
            if (restartCleanup.Count > 0)
            {
                if (restartFailure is not null) restartCleanup.Insert(0, restartFailure);
                throw new AggregateException("Companion restart verification or cleanup failed", restartCleanup);
            }
            if (restartFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(restartFailure).Throw();
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
            Require(store.CreateCompanionSnapshot(now).Providers.Single().Limits.Windows.Count == 2,
                "Published Pro 5x companion data lost the five-hour quota.");
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
            var usage = new UsagePane(store, settings, "codex", _ => { });
            popupWindow.Width = 640; popupWindow.Height = 560; popupWindow.Content = usage;
            usage.HandleShortcut(System.Windows.Input.Key.D2, System.Windows.Input.ModifierKeys.Control); await Idle();
            Require(Descendants<ProgressBar>(usage).Any(), "Owned quota did not reach the mounted Usage pane.");
            var disclosure = Descendants<System.Windows.Controls.Primitives.ToggleButton>(Descendants<Expander>(usage).Single()).Single();
            System.Windows.Input.Keyboard.Focus(disclosure);
            Require(disclosure.IsKeyboardFocusWithin, "Owner-change fixture did not focus the disclosure.");
            File.Delete(path);
            usage.RefreshLimitClock(DateTimeOffset.Now); await Idle();
            Require(store.AccountDisplay("codex") is { Label: null, Plan: null, Reading: null }, "Sign-out kept private account data visible.");
            Require(!Descendants<ProgressBar>(usage).Any() && !Descendants<TextBlock>(usage).Any(x => x.Text == "account-b@example.invalid"),
                "Focused Limits retained another owner's account label or quota after sign-out.");
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
                checks = new List<string> { "Real local auth identity matches captured reading scope", "External same-workspace switch hides old plan and limits before provider polling", "Provider row and notch popup never pair new email with old quota", "Pro hides the five-hour notch quota using raw login metadata; Pro 5x keeps it on a live ring update", "Only-quota fallback and Usage data are retained", "Companion display follows live Pro policy and ownership while preserving restart quotas", "Sign-out hides account data", "Matching Claude Max 5x/20x profile tier reaches popup without changing stored identity" } }));
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
