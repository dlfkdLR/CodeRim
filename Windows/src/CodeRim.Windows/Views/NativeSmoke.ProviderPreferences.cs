using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task ProviderPreferencesRegression(DashboardWindow dashboard, AppSettingsStore settings, string directory)
    {
        var before = settings.Current;
        Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            Require(AppSettings.Default.CostEstimatesEnabled && AppSettings.Default.CompletionSound
                && AppSettings.Default.ResetTime == "Absolute", "Fresh preferences differ from the current Mac reference.");
            var oldJson = JsonSerializer.SerializeToNode(AppSettings.Default)!.AsObject();
            oldJson.Remove("CostEstimatesEnabled"); oldJson.Remove("CompletionSound"); oldJson.Remove("ResetTime");
            var missing = oldJson.Deserialize<AppSettings>()!;
            Require(missing.CostEstimatesEnabled && missing.CompletionSound && missing.ResetTime == "Absolute",
                "Missing legacy preferences did not adopt reference defaults.");
            settings.Save(before with { CostEstimatesEnabled = false, CompletionSound = false, ResetTime = "Relative" });
            var explicitOff = new AppSettingsStore().Current;
            Require(!explicitOff.CostEstimatesEnabled && !explicitOff.CompletionSound && explicitOff.ResetTime == "Relative",
                "Default migration overwrote explicit user choices.");

            dashboard.Navigate("notch"); await Idle();
            var reset = Descendants<ComboBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Reset time");
            Require(reset.Items.Count == 2 && reset.Items[0] as string == "Absolute" && reset.Items[1] as string == "Relative", "Reset picker order differs from the reference.");
            string Label(string value)
            {
                var item = (TextBlock)reset.ItemTemplate.LoadContent(); item.DataContext = value;
                item.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget(); return item.Text;
            }
            Require(Label("Absolute") == "Reset date" && Label("Relative") == "Time remaining", "Reset picker labels differ from the reference.");
            reset.SelectedItem = "Absolute"; await Idle();
            Require(new AppSettingsStore().Current.ResetTime == "Absolute", "Reset-date selection did not persist.");

            settings.Save(settings.Current with { EnabledProviders = ["codex", "claude"], AnalyticsEnabled = true,
                SessionsEnabled = true, ProjectsEnabled = true, AgentDetailsEnabled = true, AttachmentMetadataEnabled = true, AccountLimitsEnabled = true });
            CheckBox Toggle(string name) => Descendants<CheckBox>(dashboard).Single(x => AutomationProperties.GetName(x) == name);
            foreach (var provider in new[] { "codex", "claude" })
            {
                dashboard.Navigate(provider); await Idle();
                var hasCost = Descendants<CheckBox>(dashboard).Any(x => AutomationProperties.GetName(x) == "Show estimated API-equivalent cost");
                Require(hasCost == (provider == "codex"), "An unsupported provider exposes a cost toggle.");
                Require(Toggle("Show attachment metadata").IsEnabled == (provider == "codex"), "Claude attachment controls are enabled.");
                var analytics = Toggle("Show usage analytics"); analytics.IsChecked = false; await Idle();
                Require(ReferenceEquals(analytics, Toggle("Show usage analytics")), "Analytics toggle rebuilt the provider view.");
                Require(!Toggle("Show projects").IsEnabled && !Toggle("Show sessions").IsEnabled
                    && !Toggle("Show agent details").IsEnabled && !Toggle("Show attachment metadata").IsEnabled,
                    "Disabled analytics left dependent controls enabled.");
                Require(settings.Current.ProjectsEnabled && settings.Current.SessionsEnabled && settings.Current.AgentDetailsEnabled
                    && settings.Current.AttachmentMetadataEnabled, "Disabling analytics destroyed dependent preferences.");
                analytics.IsChecked = true; await Idle();
                Require(Toggle("Show projects").IsEnabled && Toggle("Show sessions").IsEnabled && Toggle("Show agent details").IsEnabled,
                    "Analytics controls failed to recover in place.");
                var sessions = Toggle("Show sessions"); sessions.IsChecked = false; await Idle();
                Require(!Toggle("Show agent details").IsEnabled && !Toggle("Show attachment metadata").IsEnabled,
                    "Session-only controls stayed enabled when sessions were off.");
                sessions.IsChecked = true; await Idle();
                if (provider == "codex")
                {
                    Require(!Descendants<TextBlock>(dashboard).Any(x => x.Text is "Connection" or "Notch order")
                        && !Descendants<Button>(dashboard).Any(x => x.Content as string is "Refresh" or "Setup guide" or "Manage Accounts…"),
                        "Codex details retained extra sections absent from the Mac reference.");
                    Require(Descendants<TextBlock>(dashboard).Any(x => x.Text == "Add or switch accounts from the account menu in Settings ▸ Usage."),
                        "Codex details lost the account menu route.");
                    var limits = Toggle("Show account limits"); limits.IsChecked = false; await Idle();
                    Require(!Toggle("Show additional limits").IsEnabled && !Toggle("Show reset credits").IsEnabled,
                        "Disabled limits left dependent controls enabled.");
                    limits.IsChecked = true; await Idle();
                }
                Capture(dashboard, Path.Combine(directory, "windows-provider-preferences-" + provider + ".png"));
            }
            dashboard.Navigate("diagnostics"); await Idle();
            Require(Descendants<Button>(dashboard).Any(x => x.Content as string == "Choose codex.exe…")
                && Descendants<Button>(dashboard).Any(x => x.Content as string == "Open Windows setup instructions"),
                "Windows executable selection or setup recovery became unreachable.");
            File.WriteAllText(Path.Combine(directory, "windows-provider-preferences.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Fresh and missing settings use Mac defaults; explicit preferences survive reload", "Reset labels, order and persistence",
                    "Codex-only cost control", "Analytics, sessions, attachments and limits dependencies update in place without discarding preferences" } }));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { settings.Save(before); }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            try { dashboard.Navigate("usage"); await Idle(); }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
        }
        if (cleanup.Count > 0) throw new AggregateException("Provider preferences fixture cleanup failed.", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
