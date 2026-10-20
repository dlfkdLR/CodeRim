using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task ProviderDetailsRegression(DashboardWindow dashboard, DashboardStore store, AppSettingsStore settings, string directory)
    {
        var before = settings.Current; var oldReading = store.Readings.GetValueOrDefault("copilot");
        var originalWidth = dashboard.Width;
        var dark = SettingsTheme.IsDark; var contrast = SettingsTheme.IsHighContrast;
        Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            settings.Save(before with { EnabledProviders = [..before.EnabledProviders, "copilot"], AlertsEnabled = true, MutedAlertProviders = [], ResetTime = "Relative" });
            var now = DateTimeOffset.Now;
            store.Readings["copilot"] = new("copilot", ReadingState.Ready,
                [new("weekly", "Weekly", 32.6, now.AddHours(2)), new("exhausted", "Exhausted", 150),
                 new("balance", "Balance", DisplayValue: "12 USD"), new("invalid", "Unknown", double.NaN),
                 new("small", "Small", 0.3), new("nearly", "Nearly full", 99.7),
                 new("expired", "Expired", 10, now.AddMinutes(-1))], now, Plan: "pro", Account: new("fixture-a@example.invalid", "GitHub"));
            dashboard.Navigate("copilot"); await Idle();
            dashboard.UpdateLayout();
            var refresh = Descendants<Button>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "provider.refresh");
            var icon = refresh.Content as System.Windows.Shapes.Path;
            File.WriteAllText(Path.Combine(directory, "windows-provider-refresh-geometry.json"), JsonSerializer.Serialize(new {
                button = new { refresh.ActualWidth, refresh.ActualHeight, name = AutomationProperties.GetName(refresh) },
                icon = icon is null ? null : new { icon.Width, icon.Height, icon.ActualWidth, icon.ActualHeight,
                    origin = icon.TranslatePoint(new Point(), refresh), end = icon.TranslatePoint(new Point(icon.ActualWidth, icon.ActualHeight), refresh) }
            }, JsonOptions));
            Capture(dashboard, Path.Combine(directory, "windows-provider-refresh-before-assert.png"));
            Require(refresh.Content is System.Windows.Shapes.Path refreshIcon && refresh.ActualWidth == 28 && refresh.ActualHeight == 28
                && refreshIcon.Width == 16 && refreshIcon.Height == 16
                && refreshIcon.ActualWidth is > 0 and <= 16.01 && refreshIcon.ActualHeight is > 0 and <= 16.01
                && refreshIcon.TranslatePoint(new Point(), refresh).X >= -0.01
                && refreshIcon.TranslatePoint(new Point(), refresh).Y >= -0.01
                && refreshIcon.TranslatePoint(new Point(refreshIcon.ActualWidth, refreshIcon.ActualHeight), refresh).X <= refresh.ActualWidth + 0.01
                && refreshIcon.TranslatePoint(new Point(refreshIcon.ActualWidth, refreshIcon.ActualHeight), refresh).Y <= refresh.ActualHeight + 0.01
                && AutomationProperties.GetName(refresh) == "Refresh GitHub Copilot",
                "Provider refresh is not a complete accessible compact icon.");
            var identity = Descendants<StackPanel>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "provider.identity");
            TextBlock IdentityValue(string field) => Descendants<TextBlock>(identity).Single(x => AutomationProperties.GetAutomationId(x) == "provider.identity." + field);
            Require(identity.IsVisible && IdentityValue("label").Text == "fixture-a@example.invalid"
                && IdentityValue("plan").Text == "Pro" && IdentityValue("source").Text == "GitHub", "Provider account rows lost reported identity, plan or source.");
            var manage = Descendants<TextBlock>(identity).SelectMany(x => x.Inlines.OfType<System.Windows.Documents.Hyperlink>()).Single();
            Require(manage.NavigateUri.AbsoluteUri == "https://github.com/settings/copilot" && AutomationProperties.GetName(manage) == "Open usage page",
                "Provider usage link differs from the fixed Mac account destination.");
            var companion = store.CreateCompanionSnapshot(now).Providers.Single(x => x.Id == "copilot");
            Require(companion.Limits.Account is null && companion.CachedLimits?.Account?.Label == "fixture-a@example.invalid",
                "Provider account metadata leaked into public companion quotas or was discarded from the restart cache.");
            var connection = Descendants<StackPanel>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "provider.connection");
            Require(Descendants<Border>(connection).Any() && Descendants<PasswordBox>(connection).Any()
                && !Descendants<Button>(dashboard).Any(x => x.Content as string is "Refresh" or "Setup guide"),
                "Provider connection controls are not grouped or the duplicate toolbar remains.");
            var instructions = Descendants<TextBlock>(connection).SelectMany(x => x.Inlines.OfType<System.Windows.Documents.Hyperlink>()).Single();
            Require(instructions.NavigateUri.AbsoluteUri == ProviderCatalog.Find("copilot")!.GuideUrl
                && AutomationProperties.GetName(instructions) == "Connection instructions", "Connection instructions lost their original destination or accessible label.");
            var bars = Descendants<ProgressBar>(dashboard).ToArray();
            Require(bars.Length == 5 && bars.Any(x => x.Value == 32.6) && bars.Any(x => x.Value == 100), "Provider progress lost exact values or failed to clamp an exceeded limit.");
            Require(bars.All(x => x.ActualHeight == 4), "Provider usage bars differ from the compact reference.");
            var firstTrack = (FrameworkElement)bars[0].Template.FindName("PART_Track", bars[0]);
            var firstFill = (FrameworkElement)bars[0].Template.FindName("PART_Indicator", bars[0]);
            Require(firstTrack.ActualWidth > 0 && Math.Abs(firstFill.ActualWidth / firstTrack.ActualWidth - 0.326) < 0.01,
                "Rendered provider bar does not represent its percentage.");
            var ring = new ProviderRing { Settings = settings.Current with { ReduceMotion = true, ShowRemaining = false },
                Reading = new("copilot", ReadingState.Ready, [new("over", "Over", 150)], now) };
            Require((double)ring.GetValue(ProviderRing.PercentProperty) == 150 && ring.Sweep == 1 && ring.AccessibleReading() == "150% used",
                "Ring clamped its displayed or accessible reading instead of only its arc.");
            ring.Settings = ring.Settings with { ShowRemaining = true };
            ring.Reading = new("copilot", ReadingState.Ready, [new("small", "Small", 0.3)], now);
            Require(Math.Abs((double)ring.GetValue(ProviderRing.PercentProperty) - 99.7) < 0.0001 && ring.AccessibleReading() == "99.7% remaining",
                "Remaining mode rounded a real fractional remainder to 100%.");
            SettingsTheme.Apply(dark, highContrast: true); await Idle();
            Require(bars.All(x => x.Foreground is System.Windows.Media.SolidColorBrush brush && brush.Color == SystemColors.HighlightColor)
                && Descendants<ProgressBar>(dashboard).Contains(bars[0]), "High contrast did not update existing provider bars.");
            SettingsTheme.Apply(dark, contrast); await Idle();
            var texts = Descendants<TextBlock>(dashboard).Select(x => x.Text).ToArray();
            Require(texts.Contains("33% Used · 67% left", StringComparer.Ordinal) && texts.Contains("12 USD", StringComparer.Ordinal)
                && texts.Contains("—% Used · —% left", StringComparer.Ordinal)
                && texts.Contains("0.3% Used · 99.7% left", StringComparer.Ordinal)
                && texts.Contains("99.7% Used · 0.3% left", StringComparer.Ordinal)
                && texts.Contains("150% Used · 0% left", StringComparer.Ordinal), "Provider summaries lost percentages, balances or unknown states.");
            Require(texts.Count(x => x.StartsWith("Resets ", StringComparison.Ordinal)) == 1
                && !texts.Any(x => x.Contains("Resets Resets", StringComparison.Ordinal) || x.Contains("resets Resets", StringComparison.Ordinal))
                && texts.Any(x => x.StartsWith("Nearest window resets ", StringComparison.Ordinal)), "Provider resets include expired dates, duplicate prefixes or omit the nearest future reset.");
            Require(!texts.Contains("Notch order", StringComparer.Ordinal), "Provider details duplicate the list's reorder controls.");
            var alert = Descendants<Button>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "provider.alerts");
            var notify = Descendants<CheckBox>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "provider.notify");
            var draft = Descendants<PasswordBox>(dashboard).First(); draft.Password = "fixture-unsaved";
            Require(alert.IsVisible && notify.IsChecked == true, "Connected provider has no alert controls.");
            alert.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Require(settings.Current.MutedAlertProviders.Contains("copilot", StringComparer.Ordinal) && notify.IsChecked == false
                && AutomationProperties.GetName(alert) == "Unmute GitHub Copilot alerts", "Header mute did not update the persisted provider toggle.");
            notify.IsChecked = true; await Idle();
            Require(!new AppSettingsStore().Current.MutedAlertProviders.Contains("copilot", StringComparer.Ordinal), "Provider unmute did not persist.");
            settings.Save(settings.Current with { AlertsEnabled = false }); await Idle();
            Require(!alert.IsEnabled && !notify.IsEnabled && draft.Password == "fixture-unsaved"
                && Descendants<PasswordBox>(dashboard).Contains(draft), "Global alert disable left active controls or discarded credential drafts.");
            Capture(dashboard, Path.Combine(directory, "windows-provider-details.png"));
            var viewport = Descendants<ScrollViewer>(dashboard).Single(x => x.ScrollableHeight > 0 && x.ActualHeight > 200);
            viewport.ScrollToVerticalOffset(viewport.VerticalOffset + connection.TranslatePoint(new Point(), viewport).Y - 16); await Idle();
            Require(Descendants<PasswordBox>(connection).Contains(draft) && draft.ActualWidth > 80
                && Descendants<Button>(connection).All(x => x.HorizontalAlignment == HorizontalAlignment.Left),
                "Grouped connection lost its credential input or stretched its actions.");
            Capture(dashboard, Path.Combine(directory, "windows-provider-connection.png"));
            // Exercise metadata changes while a real connection draft remains mounted.
            var longLabel = new string('a', 220) + "@example.invalid";
            dashboard.Width = dashboard.MinWidth;
            store.Readings["copilot"] = store.Readings["copilot"] with { Account = new(longLabel, "api"), Plan = "enterprise" };
            settings.Save(settings.Current with { AlertsEnabled = true }); await Idle();
            viewport.ScrollToVerticalOffset(viewport.VerticalOffset + identity.TranslatePoint(new Point(), viewport).Y - 16); await Idle();
            dashboard.UpdateLayout();
            var accountLabel = IdentityValue("label");
            Require(accountLabel.Text == longLabel && accountLabel.ToolTip as string == longLabel && accountLabel.ActualWidth <= 240.01
                && accountLabel.TranslatePoint(new Point(accountLabel.ActualWidth, 0), viewport).X <= viewport.ActualWidth + 0.01
                && IdentityValue("plan").Text == "Enterprise" && IdentityValue("source").Text == "api"
                && Descendants<PasswordBox>(dashboard).Contains(draft) && draft.Password == "fixture-unsaved",
                "A long changed account overflowed the minimum window or rebuilt a connection draft.");
            Capture(dashboard, Path.Combine(directory, "windows-provider-account-min.png"));
            store.Readings["copilot"] = store.Readings["copilot"] with { Account = new(null, "api"), Plan = null };
            settings.Save(settings.Current with { AlertsEnabled = false }); await Idle();
            Require(accountLabel.Parent is FrameworkElement labelRow && labelRow.Visibility == Visibility.Collapsed
                && IdentityValue("plan").Parent is FrameworkElement planRow && planRow.Visibility == Visibility.Collapsed
                && IdentityValue("source").Text == "api", "Missing provider identity invented an account or a plan.");
            var accountContent = (StackPanel)((Border)identity.Children[1]).Child;
            Require(accountContent.Children[1].Visibility == Visibility.Collapsed && accountContent.Children[3].Visibility == Visibility.Collapsed,
                "Optional provider metadata left a leading separator.");
            store.InvalidateAccount("copilot"); await Idle();
            Require(!alert.IsVisible && identity.Visibility == Visibility.Collapsed && IdentityValue("label").Text.Length == 0
                && IdentityValue("source").Text.Length == 0 && !Descendants<ProgressBar>(dashboard).Any() && draft.Password == "fixture-unsaved", "Account invalidation retained old quotas or rebuilt credential inputs.");
            File.WriteAllText(Path.Combine(directory, "windows-provider-details.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Compact bars, exact progress, clamped overflow and nonnumeric balances", "Unknown values do not invent a bar",
                    "Future resets only and nearest reset", "Persisted header/toggle mute synchronization", "Global disable preserves drafts", "Invalidation clears old quota in place",
                    "Compact accessible refresh without duplicate toolbar", "Grouped connection preserves credential input, actions and instruction destination",
                    "Provider metadata, fixed management link and private companion cache", "Changed long and missing identities fit minimum width and preserve drafts", "Invalidation clears account fields in place" } }));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => { if (oldReading is null) store.Readings.Remove("copilot"); else store.Readings["copilot"] = oldReading; });
            Restore(() => dashboard.Width = originalWidth); Restore(() => settings.Save(before)); Restore(() => dashboard.Navigate("usage"));
            Restore(() => SettingsTheme.Apply(dark, contrast));
        }
        if (cleanup.Count > 0) throw new AggregateException("Provider details fixture cleanup failed.", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
