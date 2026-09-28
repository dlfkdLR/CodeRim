using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task NotchReflowRegression(NotchWindow notch, DashboardStore store, AppSettingsStore settings, string directory)
    {
        var observations = new List<object>();
        settings.Save(settings.Current with { Edge = NotchEdge.Right, Offset = 0, Scale = 1,
            ReduceMotion = true, Visibility = NotchVisibility.AlwaysShow, EnabledProviders = ["codex"] });
        await Idle();
        var provider = Descendants<Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.provider.codex");
        notch.Activate(); provider.Focus(); await Idle();
        Require(notch.PopupIsOpen, "Reflow fixture has no open provider card.");
        var content = notch.Content;
        notch.QueueDisplayLayout(); notch.QueueDisplayLayout(); notch.QueueDisplayLayout();
        Require(ReferenceEquals(content, notch.Content), "Display reflow ran inline before the DPI/layout change completed.");
        await Idle();
        var replacement = Descendants<Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.provider.codex");
        Require(!ReferenceEquals(content, notch.Content) && replacement.IsKeyboardFocused && notch.PopupIsOpen,
            "Display reflow lost the focused provider or its open card.");
        var providerFocusPreserved = replacement.IsKeyboardFocused;
        RequirePopupClearOfNotch(notch, "display reflow");
        notch.OpenAccounts(); await Idle();
        var accountFrame = notch.PopupContent;
        var account = Descendants<Button>(accountFrame!).First(); account.Focus(); await Idle();
        notch.QueueDisplayLayout(); await Idle();
        Require(notch.AccountMenuIsOpen && ReferenceEquals(accountFrame, notch.PopupContent) && account.IsKeyboardFocused,
            "Display reflow dismissed or replaced an active account menu.");
        observations.Add(new { stage = "reflow", providerFocus = providerFocusPreserved,
            accountFocus = account.IsKeyboardFocused, accountMenuPreserved = notch.AccountMenuIsOpen });
        Keyboard.ClearFocus();
        foreach (var edge in Enum.GetValues<NotchEdge>())
        {
            settings.Save(settings.Current with { Edge = edge, Scale = 1.25, EnabledProviders = ProviderCatalog.All.Select(x => x.Id).ToArray() });
            Require(store.Synthetic, "Routed wheel fixture must use synthetic providers.");
            await store.RefreshAsync(true);
            await Idle();
            var scroll = Descendants<ScrollViewer>(notch).Single();
            var vertical = edge is NotchEdge.Left or NotchEdge.Right;
            scroll.ScrollToHome(); await Idle();
            var before = vertical ? scroll.VerticalOffset : scroll.HorizontalOffset;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            scroll.RaiseEvent(wheel); await Idle();
            var after = vertical ? scroll.VerticalOffset : scroll.HorizontalOffset;
            Require(wheel.Handled && after > before, "Routed wheel did not scroll providers on " + edge);
            notch.QueueDisplayLayout(); await Idle();
            var current = Descendants<ScrollViewer>(notch).Single();
            var retained = vertical ? current.VerticalOffset : current.HorizontalOffset;
            Require(Math.Abs(retained - after) < 1, "Display reflow reset provider scroll on " + edge);
            observations.Add(new { stage = "routed-wheel", edge, before, after, retained, wheel.Handled });
        }
        File.WriteAllText(Path.Combine(directory, "windows-notch-reflow.json"),
            JsonSerializer.Serialize(new { completed = true, physicalInput = false, observations }, JsonOptions));
    }
}
