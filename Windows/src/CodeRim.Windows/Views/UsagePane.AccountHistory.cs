using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class UsagePane
{
    private const string AccountHistoryHelp = "ChatGPT account totals include synced local and cloud usage. Local records are shown separately and are never added again to the server total.";
    private void AddAccountHistoryFooter()
    {
        if (provider != "codex") return;
        var footer = new StackPanel { Margin = new Thickness(0, 0, 0, 12), ToolTip = AccountHistoryHelp };
        AutomationProperties.SetAutomationId(footer, "history.account.footer");
        if (!store.ProfileHistory.Enabled)
        {
            var enable = HistoryLink("Include ChatGPT history", () =>
            {
                try { settings.Save(settings.Current with { ProfileSyncEnabled = true }); }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
                { MessageBox.Show("Unable to save the account history preference. Please try again.", "CodeRim"); }
            });
            AutomationProperties.SetAutomationId(enable, "history.account.enable"); footer.Children.Add(enable);
        }
        else
        {
            if (store.ProfileHistory.Snapshot is { } snapshot)
            {
                footer.Children.Add(HistoryNote("Server through " + ProfileDate(snapshot.StatsAsOf) + " · Includes synced local and cloud usage"));
                if (store.ProfileHistory.Status != ProfileUsageStatus.Ready) footer.Children.Add(HistoryNote(store.ProfileHistory.Message));
            }
            else footer.Children.Add(HistoryNote(store.ProfileHistory.Message));
            footer.Children.Add(HistoryNote("Recent local usage appears after the next server update."));
            var local = HistoryLink("Local History on this PC", () => Forward("local-period", "all-time"));
            AutomationProperties.SetAutomationId(local, "history.local.details"); footer.Children.Add(local);
        }
        readings.Children.Add(footer);
    }
    private static string ProfileDate(DateOnly date) => date.ToString("MMM d", CultureInfo.CurrentCulture);
    private static TextBlock HistoryNote(string text)
    {
        var note = Ui.Text(text, 11, "#A6A6AA"); note.Margin = new Thickness(0, 0, 0, 8); return note;
    }
    private static Button HistoryLink(string title, Action action)
    {
        var link = Ui.Button(title, action); link.FontSize = 11; link.Background = System.Windows.Media.Brushes.Transparent;
        link.BorderThickness = new Thickness(0); link.Padding = new Thickness(0); link.MinHeight = 16; link.Margin = new Thickness(0);
        link.HorizontalAlignment = HorizontalAlignment.Left; link.SetResourceReference(Control.ForegroundProperty, "SecondaryText"); return link;
    }
    private void PeriodDetail()
    {
        var snapshot = store.Usage.GetValueOrDefault(provider) ?? UsageSnapshot.Empty;
        var usage = period switch { "week" => snapshot.Week, "month" => snapshot.Month, "all-time" => snapshot.AllTime, _ => snapshot.Today };
        var account = destination == "account-period";
        var profile = provider == "codex" && store.ProfileHistory.Enabled ? store.ProfileHistory.Snapshot : null;
        long? total = account ? period switch { "week" => profile?.Week, "month" => profile?.Month, "all-time" => profile?.Lifetime, _ => null }
            : snapshot.UpdatedAt is null ? null : usage.TotalTokens;
        var scope = account ? "ChatGPT account" : "This PC";
        readings.Children.Add(Ui.Text(scope, 11, "#A6A6AA", FontWeights.SemiBold));
        var totalView = total is { } count ? (FrameworkElement)Metric(destination + ":" + period, count, 30) : Ui.Text("—", 30, weight: FontWeights.SemiBold);
        AutomationProperties.SetAutomationId(totalView, "usage.period.total");
        AutomationProperties.SetName(totalView, scope + " " + DetailTitle() + " total tokens, " + (total?.ToString(CultureInfo.CurrentCulture) ?? "unavailable"));
        readings.Children.Add(totalView);
        readings.Children.Add(HistoryNote(account ? profile is null ? "Account totals unavailable" : "Server total through " + ProfileDate(profile.StatsAsOf) : "This PC total tokens"));
        readings.Children.Add(SettingsUi.Divider());
        if (snapshot.UpdatedAt is null)
        {
            readings.Children.Add(Ui.Text(store.IsRefreshing ? "Reading this PC's usage" : "No local usage found", 13, weight: FontWeights.SemiBold));
            readings.Children.Add(HistoryNote(store.Status));
        }
        else
        {
            if (account) readings.Children.Add(Ui.Row("This PC " + (period switch { "week" => "this week", "month" => "this month", "all-time" => "local history", _ => "today" }), Core.Services.TokenFormatter.Format(usage.TotalTokens, settings.Current.NumberStyle)));
            readings.Children.Add(Ui.Row("Input", Core.Services.TokenFormatter.Format(usage.InputTokens, settings.Current.NumberStyle)));
            if (settings.Current.ShowCachedInput) readings.Children.Add(Ui.Row("Cached input", Core.Services.TokenFormatter.Format(usage.CachedInputTokens, settings.Current.NumberStyle)));
            readings.Children.Add(Ui.Row("Output", Core.Services.TokenFormatter.Format(usage.OutputTokens, settings.Current.NumberStyle)));
        }
        readings.Children.Add(HistoryNote(account ? profile is null ? store.ProfileHistory.Message : "Profile through " + ProfileDate(profile.StatsAsOf)
            + (store.ProfileHistory.Status == ProfileUsageStatus.Ready ? "" : " · " + store.ProfileHistory.Message) : store.Status));
    }
}
