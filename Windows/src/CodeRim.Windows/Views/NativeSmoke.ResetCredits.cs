using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task ResetCreditsRegression(DashboardWindow dashboard, NotchWindow notch, DashboardStore store, AppSettingsStore settings, string directory)
    {
        var before = settings.Current; var previous = store.Readings["codex"];
        UsagePane? usage = null; var wasLimits = false;
        Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            settings.Save(before with { EnabledProviders = ["codex"], AccountLimitsEnabled = true, ResetCreditsEnabled = true });
            dashboard.Navigate("usage"); await Idle();
            var pane = usage = Descendants<UsagePane>(dashboard).Single(); wasLimits = pane.ShowsLimits;
            pane.SelectProvider("codex"); pane.HandleShortcut(Key.D2, ModifierKeys.Control);
            foreach (var style in new[] { TokenNumberStyle.Compact, TokenNumberStyle.Detailed })
            foreach (var variant in new[] { "zero", "numeric", "large", "unlimited", "available", "expired" })
            {
                settings.Save(settings.Current with { NumberStyle = style });
                var count = variant switch { "zero" => 0L, "numeric" => 2L, "large" => 2000L, _ => (long?)null };
                var text = variant switch { "unlimited" => "Unlimited resets", "available" => "Reset count unavailable", _ => null };
                var expiry = variant switch { "available" => DateTimeOffset.Now.AddDays(3), "expired" => DateTimeOffset.Now.AddDays(-3), _ => (DateTimeOffset?)null };
                var credit = new LimitWindow(ProviderDisplayPolicy.ResetCreditsId, "Reset credits", ResetsAt: expiry,
                    RemainingCount: count, Unit: "resets", DisplayValue: text);
                store.Readings["codex"] = new("codex", ReadingState.Ready, [new("weekly", "Weekly", 20), credit], DateTimeOffset.Now);
                pane.Update(); await Idle();
                var card = Descendants<Border>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.reset-credits");
                var value = Descendants<TextBlock>(card).Single(x => AutomationProperties.GetAutomationId(x) == "usage.reset-credits.value");
                var expected = count?.ToString("N0", CultureInfo.CurrentCulture) ?? (variant == "unlimited" ? "Unlimited" : "Available");
                Require(value.Text == expected && Descendants<TextBlock>(pane).Count(x => x.Text == "Reset credits") == 1,
                    "Usage reset credit value is duplicated, abbreviated or missing: " + variant + "/" + style);
                Require(!Descendants<Button>(card).Any(), "Read-only reset credits contain a consuming action.");
                Require(Descendants<TextBlock>(card).Count(x => AutomationProperties.GetAutomationId(x) == "usage.reset-credits.expiration") == (expiry is null ? 0 : 1),
                    "Reset credit expiration did not follow the reported value.");
                if (expiry is { } date)
                {
                    var expiration = Descendants<TextBlock>(card).Single(x => AutomationProperties.GetAutomationId(x) == "usage.reset-credits.expiration");
                    Require(expiration.Text.Contains(date.ToLocalTime().ToString("dd", CultureInfo.CurrentCulture).TrimStart('0'), StringComparison.Ordinal)
                        && expiration.Text.Contains(date.ToLocalTime().ToString("MMM", CultureInfo.CurrentCulture), StringComparison.Ordinal),
                        "Reset credit expiration lost the reported local month or day.");
                }
                card.BringIntoView(); await Idle();
                Require(card.ActualWidth > 0 && value.ActualWidth > 0 && value.TranslatePoint(new Point(value.ActualWidth, value.ActualHeight), card).X <= card.ActualWidth,
                    "Reset credit value is clipped in its rendered row.");
                notch.OpenProvider("codex"); await Idle();
                Require(!Descendants<TextBlock>(notch.PopupContent!).Any(x => x.Text == "Reset credits" || x.Text == "Unlimited resets" || x.Text == "Reset count unavailable"),
                    "Reset credits entered the quota-only notch snapshot.");
                Require(!Descendants<TextBlock>(notch.PopupContent!).Any(x => x.Text == "Stale"), "Credit expiration incorrectly marked fresh notch quotas stale.");
                if (style == TokenNumberStyle.Detailed && variant is "numeric" or "available")
                    Capture(dashboard, Path.Combine(directory, "windows-reset-credits-" + variant + ".png"));
            }
            settings.Save(settings.Current with { ResetCreditsEnabled = false }); pane.Update(); await Idle();
            Require(!Descendants<Border>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.reset-credits"), "Reset credit preference did not hide the row.");
            settings.Save(settings.Current with { ResetCreditsEnabled = true, AccountLimitsEnabled = false }); pane.Update(); await Idle();
            Require(!Descendants<Border>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.reset-credits"), "Disabled account limits retained reset credits.");
            File.WriteAllText(Path.Combine(directory, "windows-reset-credits.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "One read-only Usage row", "Exact zero, numeric and large counts in both number styles", "Unlimited and available-with-expiry",
                    "Quota-only notch omits credits", "Reset-credit and account-limit preferences hide the row" } }));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => store.Readings["codex"] = previous); Restore(() => settings.Save(before));
            Restore(() => usage?.HandleShortcut(wasLimits ? Key.D2 : Key.D1, ModifierKeys.Control));
            Restore(() => dashboard.Navigate("notch"));
        }
        if (cleanup.Count > 0) throw new AggregateException("Reset credit fixture cleanup failed.", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
