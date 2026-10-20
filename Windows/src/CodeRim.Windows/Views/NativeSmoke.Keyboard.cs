using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;

namespace CodeRim.Windows.Views;
internal static partial class NativeSmoke
{
    private static async Task CheckNotchKeyboardAccounts(NotchWindow notch, string directory)
    {
        var gear = Descendants<Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.settings");
        // Exercise WPF's actual focus traversal. This is native keyboard navigation
        // state, not evidence that a physical key was delivered by the desktop.
        notch.Activate(); gear.Focus(); await Idle();
        Require(gear.IsKeyboardFocused && notch.ControlsRevealed, "Keyboard focus did not reveal settings controls");
        gear.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); await Idle();
        var trigger = Descendants<Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.switchAccount");
        Require(trigger.IsKeyboardFocused, "Tab traversal did not reach the account trigger");
        trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
        var menu = notch.PopupContent ?? throw new InvalidOperationException("Keyboard account menu has no content");
        var rows = Descendants<Button>(menu).Where(x => x.IsEnabled && x.IsVisible).ToArray();
        Require(rows.Length == 2, "Keyboard fixture must contain two independently focusable provider rows");
        var entered = rows[0].IsKeyboardFocused;
        File.WriteAllText(Path.Combine(directory, "windows-notch-keyboard.json"),
            JsonSerializer.Serialize(new { entered, stage = "menu entry", physicalKeys = false }, JsonOptions));
        Require(entered, "Opening the account menu from keyboard did not focus its first row");
        rows[0].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); await Idle();
        Require(rows[1].IsKeyboardFocused, "Tab did not reach the second account row");
        rows[1].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); await Idle();
        Require(rows[0].IsKeyboardFocused, "Tab escaped the account menu instead of cycling");
        rows[0].MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)); await Idle();
        Require(rows[1].IsKeyboardFocused, "Reverse Tab escaped the menu instead of reaching the last row");
        rows[1].MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)); await Idle();
        Require(rows[0].IsKeyboardFocused, "Reverse Tab did not return to the first account row");
        var source = PresentationSource.FromVisual(menu) ?? throw new InvalidOperationException("Account menu has no presentation source");
        menu.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await Idle();
        Require(!notch.PopupIsOpen && trigger.IsKeyboardFocused && notch.ControlsRevealed,
            "Escape did not restore keyboard focus to the account trigger");
        File.WriteAllText(Path.Combine(directory, "windows-notch-keyboard.json"),
            JsonSerializer.Serialize(new { entered, rowCount = rows.Length, movedToSecond = true, cycled = true, reverseCycled = true, escapeReturnedFocus = true, physicalKeys = false, outcome = "PASS" }, JsonOptions));
        Keyboard.ClearFocus();
    }
}
