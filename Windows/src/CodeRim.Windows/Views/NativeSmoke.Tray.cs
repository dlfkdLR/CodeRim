using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.Tray;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostTrayKey(IntPtr window, uint message, IntPtr key, IntPtr data);
    private static readonly string[] TrayLabels = ["Token Usage…", "Show Notch", "Settings…", "Check for Updates…", "Quit CodeRim"];
    private static async Task TrayRegression(DashboardWindow dashboard, AppSettingsStore settings, string directory)
    {
        Require(Path.GetFileName(CompanionFile.DataDirectory).StartsWith("CodeRim-Smoke-", StringComparison.Ordinal), "Tray QA requires isolated settings");
        var app = (App)Application.Current;
        var tray = app.Tray ?? throw new InvalidOperationException("The application has no tray");
        var original = settings.Current; var temporary = Path.Combine(CompanionFile.DataDirectory, "settings.json.new");
        var createdTemporary = false; Exception? failure = null; var cleanup = new List<Exception>(); var checks = new List<string>();
        var menu = tray.Menu;
        Forms.ToolStripMenuItem Item(int index) => (Forms.ToolStripMenuItem)menu.Items[index];
        try
        {
            Require(menu.Items.Count == 7 && menu.Items[1] is Forms.ToolStripSeparator && menu.Items[5] is Forms.ToolStripSeparator, "Tray group structure differs from Mac");
            Require(menu.Items.OfType<Forms.ToolStripMenuItem>().Select(x => x.Text).SequenceEqual(TrayLabels), "Tray menu labels or order differ from Mac");
            Require(Item(0).ShortcutKeys == (Forms.Keys.Control | Forms.Keys.U) && Item(3).ShortcutKeys == (Forms.Keys.Control | Forms.Keys.Oemcomma)
                && Item(6).ShortcutKeys == (Forms.Keys.Control | Forms.Keys.Q), "Tray shortcuts lost Windows equivalents");
            checks.Add("Mac menu order, labels, groups, and Windows shortcut equivalents");

            dashboard.Navigate("notch"); await Idle();
            var before = Descendants<CheckBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Show edge notch");
            dashboard.WindowState = WindowState.Minimized; Item(3).PerformClick(); await Idle();
            Require(dashboard.WindowState != WindowState.Minimized && Descendants<CheckBox>(dashboard).Contains(before), "Settings reopened a different page or rebuilt the active view");
            Item(0).PerformClick(); await Idle();
            Require(Descendants<UsagePane>(dashboard).Any(), "Token Usage did not open the real Usage screen");
            checks.Add("Actual application Settings restores the existing view and Token Usage navigates to Usage");

            dashboard.Navigate("notch"); await Idle();
            var showToggle = Descendants<CheckBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Show edge notch");
            var behaviour = Descendants<ComboBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Behaviour");
            foreach (var mode in new[] { NotchVisibility.OnHover, NotchVisibility.AlwaysShow })
            {
                settings.Save(settings.Current with { Visibility = mode });
                Require(Item(2).Checked && showToggle.IsChecked == true && behaviour.IsEnabled && Equals(behaviour.SelectedItem, mode), "External visibility/mode did not reach tray and mounted Settings");
                Item(2).PerformClick(); await Idle();
                Require(settings.Current.Visibility == NotchVisibility.Hidden && !Item(2).Checked && showToggle.IsChecked == false && !behaviour.IsEnabled, "Show Notch failed to hide or synchronize Settings");
                Item(2).PerformClick(); await Idle();
                Require(settings.Current.Visibility == mode && Item(2).Checked && showToggle.IsChecked == true && behaviour.IsEnabled && Equals(behaviour.SelectedItem, mode) && new AppSettingsStore().Current.Visibility == mode, "Show Notch failed to restore and persist its previous mode");
            }
            settings.Save(settings.Current with { Visibility = NotchVisibility.Hidden });
            Require(!Item(2).Checked, "External hide did not clear the tray check");
            checks.Add("Show Notch toggles persisted visibility and restores both hover and always-visible modes while synchronizing mounted Settings");

            Require(!File.Exists(temporary) && !Directory.Exists(temporary), "Settings failure fixture path is occupied");
            Directory.CreateDirectory(temporary); createdTemporary = true;
            Item(2).PerformClick();
            Require(settings.Current.Visibility == NotchVisibility.Hidden && !Item(2).Checked, "Failed save changed the menu check or live visibility");
            Directory.Delete(temporary); createdTemporary = false;
            checks.Add("Settings write failure remains nonfatal and cannot falsely check Show Notch");

            var usage = 0; var toggle = 0; var settingsAction = 0; var updates = 0; var quit = 0; var enabled = false;
            using (var commands = new TrayIconHost(() => usage++, () => toggle++, () => settingsAction++, () => updates++, () => quit++, () => false, () => enabled))
            {
                foreach (var item in commands.Menu.Items.OfType<Forms.ToolStripMenuItem>()) item.PerformClick();
                Require(usage == 1 && toggle == 1 && settingsAction == 1 && updates == 0 && quit == 1, "Tray commands were misrouted or disabled update executed");
                enabled = true; commands.RefreshState(); commands.Menu.Items[4].PerformClick();
                Require(updates == 1, "Enabled update action did not execute exactly once");
            }
            checks.Add("Five action callbacks and update availability guard (no live update or process exit)");

            foreach (var dark in new[] { true, false })
            {
                SettingsTheme.Apply(dark); tray.RefreshAppearance();
                var screen = Forms.Screen.PrimaryScreen!.WorkingArea;
                tray.ShowMenu(new Drawing.Point(screen.Right - 10, screen.Bottom - 10)); await Idle();
                Require(menu.Visible && screen.Contains(menu.Bounds), "Tray popup extends beyond the working area");
                Require(menu.Items.OfType<Forms.ToolStripMenuItem>().All(x => x.Bounds.Width <= menu.Width), "Tray action is clipped");
                menu.Items[3].Select(); menu.Refresh();
                using var capture = new Drawing.Bitmap(menu.Width, menu.Height);
                menu.DrawToBitmap(capture, new Drawing.Rectangle(Drawing.Point.Empty, menu.Size));
                capture.Save(Path.Combine(directory, dark ? "windows-tray-dark.png" : "windows-tray-light.png"));
                tray.ShowMenu(Drawing.Point.Empty); await Idle();
                Require(!menu.Visible, "Repeated tray activation did not close the popup");
            }
            checks.Add("Mounted dark/light menus render selected row and remain inside the screen at its lower-right edge");
            Forms.ToolStripDropDownCloseReason? closeReason = null;
            void Closed(object? sender, Forms.ToolStripDropDownClosedEventArgs args) => closeReason = args.CloseReason;
            menu.Closed += Closed;
            try
            {
                tray.ShowMenu(new Drawing.Point(100, 100)); await Idle();
                Require(menu.Visible && closeReason is null, "Tray menu closed before the Escape probe");
                Require(PostTrayKey(menu.Handle, 0x100, new IntPtr(0x1B), IntPtr.Zero), "Could not queue Escape to the tray menu");
                await Task.Delay(120); await Idle();
                Require(!menu.Visible && closeReason == Forms.ToolStripDropDownCloseReason.Keyboard, "Escape did not dismiss the tray menu through the WPF/Forms message loop");
            }
            finally { menu.Closed -= Closed; }
            checks.Add("Escape reaches the menu through the native message queue and WPF/Forms keyboard interop");
            foreach (var size in new[] { 16, 20, 24, 32, 48 })
            {
                using var mark = TrayMark.Render(size, Drawing.Color.White);
                Require(mark.GetPixel(size / 2, size / 2).A == 0 && mark.GetPixel(0, 0).A == 0, "Tray mark gained an opaque center/background");
                Require(mark.GetPixel(size / 2, 2 * size / 18).A > 0 && mark.GetPixel(size - 2 * size / 18 - 1, size / 2).A > 0, "Tray mark lost the large or detached segment");
                mark.Save(Path.Combine(directory, "windows-tray-mark-" + size + ".png"));
            }
            checks.Add("Both mark segments and transparent center/background at five DPI icon sizes");
            File.WriteAllText(Path.Combine(directory, "windows-tray-reference.json"), JsonSerializer.Serialize(new { completed = true, checks,
                physicalTrayClick = false, realUpdateInstall = false, evidence = "Native menu rendering and actual app routed commands; separate callback probe for update/quit" }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => menu.Close()); Restore(() => { if (createdTemporary) Directory.Delete(temporary); });
            Restore(() => settings.Save(original)); Restore(() => { SettingsTheme.Apply(); tray.RefreshAppearance(); }); Restore(() => dashboard.Navigate("usage"));
        }
        if (cleanup.Count > 0) throw new AggregateException("Tray fixture cleanup failed", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
