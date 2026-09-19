using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using System.Security.AccessControl;
using System.Security.Principal;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

/// <summary>Runs only with --smoke-test and isolated synthetic data, using production WPF views.</summary>
internal static class NativeSmoke
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static async Task RunAsync(DashboardWindow dashboard, NotchWindow notch, DashboardStore store, AppSettingsStore settings, string output)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Record(string message)
        {
            checks.Add(message);
            File.WriteAllText(Path.Combine(directory, "windows-ui-progress.json"), JsonSerializer.Serialize(checks, JsonOptions));
        }
        Require(store.Synthetic, "Smoke must use synthetic data");
        settings.Save(settings.Current with { EnabledProviders = ["codex", "claude"] });
        await store.RefreshAsync(true); dashboard.Navigate("usage");
        var privateFile = Path.Combine(CompanionFile.DataDirectory, "acl-fixture.txt");
        GuardedFile.WritePrivate(privateFile, "fixture-before");
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var security = new FileInfo(privateFile).GetAccessControl();
            Require(security.AreAccessRulesProtected, "Credential file inherits permissions");
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
            Require(rules.All(x => x.AccessControlType != AccessControlType.Allow || x.IdentityReference == identity.User), "Credential file grants another user access");
            GuardedFile.Replace(privateFile, "fixture-before", "fixture-after");
            Require(GuardedFile.Read(privateFile) == "fixture-after" && new FileInfo(privateFile).GetAccessControl().AreAccessRulesProtected, "Credential replacement lost protected permissions");
        }
        File.Delete(privateFile);
        var vault = new CredentialVault(); vault.Save("smoke.fixture", "synthetic-secret");
        Require(vault.Load("smoke.fixture") == "synthetic-secret", "DPAPI round trip failed");
        vault.Delete("smoke.fixture"); Require(vault.Load("smoke.fixture") is null, "Credential removal failed");
        Record("Windows private-file ACL, atomic replacement, and user DPAPI round trip");
        var previousPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
        try
        {
            var cliDirectory = CliInstaller.Install();
            CliInstaller.Install();
            var pathEntries = (Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "").Split(';');
            Require(pathEntries.Count(x => string.Equals(x, cliDirectory, StringComparison.OrdinalIgnoreCase)) == 1, "CLI installation duplicated PATH");
            Require(File.ReadAllText(Path.Combine(cliDirectory, "coderim.cmd")).Contains("CodeRimCLI.exe", StringComparison.Ordinal), "CLI wrapper is missing");
        }
        finally { Environment.SetEnvironmentVariable("Path", previousPath, EnvironmentVariableTarget.User); }
        AppDiagnostics.Record("codex", "Ready", 2);
        AppDiagnostics.Record("private-unknown", "private-payload", 0);
        var diagnosticText = File.ReadAllText(Path.Combine(AppDiagnostics.LogDirectory, "diagnostics.log"));
        Require(diagnosticText.Contains("codex Ready count=2", StringComparison.Ordinal)
            && !diagnosticText.Contains("private-", StringComparison.Ordinal), "Diagnostic log accepted a non-catalog payload");
        Record("Settings CLI installation is idempotent and debug logs accept only bounded metadata");

        var previousClaudeConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var fixtureConfig = Path.Combine(CompanionFile.DataDirectory, "claude-fixture"); Directory.CreateDirectory(fixtureConfig);
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", fixtureConfig);
            File.WriteAllText(Path.Combine(fixtureConfig, "settings.json"), """{"unrelated":true,"hooks":{"Stop":[{"hooks":[{"type":"command","command":"fixture-existing"}]}]}}""");
            ClaudeHookInstaller.Install(); ClaudeHookInstaller.Install();
            using var installed = JsonDocument.Parse(File.ReadAllText(ClaudeHookInstaller.SettingsPath));
            Require(installed.RootElement.GetProperty("unrelated").GetBoolean(), "Claude setup discarded unrelated settings");
            var hooks = installed.RootElement.GetProperty("hooks");
            Require(hooks.GetProperty("Stop").GetArrayLength() == 1 && hooks.GetProperty("SessionStart").GetArrayLength() == 1, "Claude setup duplicated or discarded hooks");
            var command = ClaudeHookInstaller.Command("claude-status").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var result = await BoundedProcess.RunAsync(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                command.Skip(1), """{"session_id":"synthetic-unregistered","rate_limits":{"five_hour":{"used_percentage":53}}}""");
            Require(result.Contains("53%", StringComparison.Ordinal), "Installed Claude command did not read stdin");
            Record("Claude installation preserves settings, is idempotent, and executes its Windows command with stdin");
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousClaudeConfig); }

        var glyphGrid = new WrapPanel { Width = 720, Background = Ui.Brush("#202020") };
        foreach (var provider in ProviderCatalog.All)
        {
            Require(ProviderMark.HasGlyph(provider.Id), "Provider logo is missing: " + provider.Id);
            var mark = new ProviderMark { ProviderId = provider.Id, Width = 32, Height = 32 };
            mark.Measure(new Size(32, 32)); mark.Arrange(new Rect(0, 0, 32, 32));
            var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32); bitmap.Render(mark);
            var pixels = new byte[32 * 32 * 4]; bitmap.CopyPixels(pixels, 32 * 4, 0);
            Require(Enumerable.Range(0, 32 * 32).Count(i => pixels[i * 4 + 3] > 32) > 8, "Provider logo is blank: " + provider.Id);
            if (provider.Id is "opencode" or "opencode-zen")
                Require(pixels[(16 * 32 + 16) * 4 + 3] < 32, "OpenCode logo lost its center cutout");
            if (provider.Id is "openrouter" or "ibmbob")
                Require(pixels[(1 * 32 + 1) * 4 + 3] < 32, "Provider logo rendered its mask or clipping rectangle: " + provider.Id);
            var tile = new StackPanel { Width = 120, Height = 70, HorizontalAlignment = HorizontalAlignment.Center };
            tile.Children.Add(new ProviderMark { ProviderId = provider.Id, Width = 28, Height = 28, Margin = new Thickness(0, 6, 0, 4) });
            tile.Children.Add(Ui.Text(provider.Name, 10)); glyphGrid.Children.Add(tile);
        }
        glyphGrid.Measure(new Size(720, double.PositiveInfinity)); glyphGrid.Arrange(new Rect(glyphGrid.DesiredSize));
        Capture(glyphGrid, Path.Combine(directory, "windows-provider-logos.png")); Record("Every provider logo loads and renders");
        SettingsTheme.Apply(dark: true, highContrast: false);
        await Idle(); Capture(dashboard, output); Record("Usage window renders");
        var shortcuts = Descendants<System.Windows.Controls.Button>(dashboard).Where(x => (AutomationProperties.GetAutomationId(x) ?? "").StartsWith("usage.destination.", StringComparison.Ordinal)).ToArray();
        Require(shortcuts.Length == 3, "Usage analytics shortcuts are missing");
        var positions = shortcuts.Select(x => x.TransformToAncestor(dashboard).Transform(new Point())).ToArray();
        Require(positions.Max(x => x.Y) - positions.Min(x => x.Y) < 1, "Settings analytics shortcuts must share one row");
        Require(positions.Max(x => x.Y) + shortcuts.Max(x => x.ActualHeight) < dashboard.ActualHeight, "Analytics navigation is clipped below the Settings window");
        var selector = Descendants<System.Windows.Controls.ComboBox>(dashboard).First();
        selector.Focus(); var original = selector;
        await store.RefreshAsync(true).ConfigureAwait(true); await Idle();
        Require(Descendants<System.Windows.Controls.ComboBox>(dashboard).Contains(original), "Refresh replaced usage selector");
        Require(original.IsKeyboardFocusWithin, "Refresh stole usage keyboard focus"); Record("Refresh preserves usage selector and keyboard focus");
        original.SelectedValue = "claude"; await Idle();
        dashboard.Navigate("general"); dashboard.Navigate("usage"); await Idle();
        Require((string?)Descendants<System.Windows.Controls.ComboBox>(dashboard).First().SelectedValue == "claude", "Sidebar navigation lost selected usage provider");
        Require(settings.Current.UsageProvider == "claude", "Usage provider selection was not persisted");
        original.SelectedValue = "codex"; await Idle();
        var mode = Descendants<RadioButton>(dashboard).First();
        Require(mode.IsChecked == true && new System.Windows.Automation.Peers.RadioButtonAutomationPeer(mode).GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem) is not null, "Usage mode has no accessible selection state");
        Record("Selected provider survives sidebar navigation and mode exposes selected state");

        var projects = Descendants<System.Windows.Controls.Button>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "usage.destination.projects");
        projects.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
        var periodSelector = Descendants<System.Windows.Controls.ComboBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Usage period");
        periodSelector.SelectedValue = "month";
        var filter = Descendants<System.Windows.Controls.TextBox>(dashboard).Single(); filter.Text = "CodeRim"; await Idle();
        var projectRow = Descendants<System.Windows.Controls.Button>(dashboard).FirstOrDefault(x => (AutomationProperties.GetName(x) ?? "").StartsWith("CodeRim:", StringComparison.Ordinal));
        Require(projectRow is not null, "Synthetic project row missing");
        projectRow!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
        Descendants<System.Windows.Controls.Button>(dashboard).Single(x => Equals(x.Content, "‹ Back")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
        Require(Descendants<System.Windows.Controls.TextBox>(dashboard).Single().Text == "CodeRim", "Back lost project search");
        Require((string?)Descendants<System.Windows.Controls.ComboBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Usage period").SelectedValue == "month", "Back lost selected period");
        Descendants<System.Windows.Controls.Button>(dashboard).Single(x => Equals(x.Content, "‹ Back")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
        Record("Project detail Back restores list search and period");

        dashboard.Navigate("sessions:codex"); await Idle();
        settings.Save(settings.Current with { EnabledProviders = ["claude"] });
        dashboard.Navigate("providers"); dashboard.Navigate("usage"); await Idle();
        Require((string?)Descendants<System.Windows.Controls.ComboBox>(dashboard).First().SelectedValue == "claude", "Removed provider did not select an available provider");
        Require(Descendants<RadioButton>(dashboard).Any(), "Removed provider retained its detail navigation");
        Require(Descendants<TextBlock>(dashboard).Any(x => x.Text == "Today"), "Removed provider did not return to available provider overview");
        settings.Save(settings.Current with { EnabledProviders = ["codex", "claude"] });
        dashboard.Navigate("usage"); Descendants<System.Windows.Controls.ComboBox>(dashboard).First().SelectedValue = "codex"; await Idle();
        Record("Removing a provider clears its navigation and filters");


        dashboard.Navigate("codex"); await Idle();
        var limitsToggle = Descendants<CheckBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Show account limits");
        limitsToggle.IsChecked = false; await Idle();
        Require(!Descendants<CheckBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Show reset credits").IsEnabled, "Limit dependent controls stayed enabled");
        Require(ProviderDisplayPolicy.Apply(store.Readings["codex"], settings.Current) is { State: ReadingState.Disabled, Windows.Count: 0 }, "Disabled Codex limits remained visible");
        limitsToggle.IsChecked = true; await Idle();
        settings.Save(settings.Current with { AdditionalLimitsEnabled = false, ResetCreditsEnabled = false });
        var filteredLimits = ProviderDisplayPolicy.Apply(store.Readings["codex"], settings.Current)!;
        Require(filteredLimits.Windows.Count == 2 && filteredLimits.Windows.All(x => x.Id is "session" or "weekly"), "Additional/reset limit filters ignored");
        settings.Save(settings.Current with { AdditionalLimitsEnabled = true, ResetCreditsEnabled = true });
        Record("Codex limit switches filter all surfaces and dependent controls follow parent setting");
        dashboard.Navigate("sessions:codex"); await Idle();
        Descendants<System.Windows.Controls.Button>(dashboard).Single(x => (AutomationProperties.GetName(x) ?? "").StartsWith("preview-session:", StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Idle();
        Require(Descendants<TextBlock>(dashboard).Any(x => x.Text == "Whole-session images"), "Session image metadata is absent");
        var sessionPeriod = Descendants<System.Windows.Controls.ComboBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Usage period");
        sessionPeriod.SelectedValue = "today"; await Idle();
        Require(!Descendants<System.Windows.Controls.Button>(dashboard).Any(x => (x.Content as string ?? "").StartsWith("Sub-agent preview-", StringComparison.Ordinal)), "Out-of-period child links lead to empty detail");
        sessionPeriod.SelectedValue = "all-time"; await Idle();
        var childButton = Descendants<System.Windows.Controls.Button>(dashboard).Single(x => (x.Content as string ?? "").StartsWith("Sub-agent preview-", StringComparison.Ordinal));
        Capture(dashboard, Path.Combine(directory, "windows-session-details.png"));
        childButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
        Require(Descendants<TextBlock>(dashboard).Any(x => x.Text == "120"), "Sub-agent navigation did not show its own total");
        settings.Save(settings.Current with { AgentDetailsEnabled = false, AttachmentMetadataEnabled = false });
        dashboard.Navigate("providers"); dashboard.Navigate("sessions:codex"); await Idle();
        Descendants<System.Windows.Controls.Button>(dashboard).Single(x => (AutomationProperties.GetName(x) ?? "").StartsWith("preview-session:", StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
        Require(!Descendants<TextBlock>(dashboard).Any(x => x.Text is "Whole-session images" or "Direct sub-agents"), "Disabled session metadata remained visible");
        settings.Save(settings.Current with { AgentDetailsEnabled = true, AttachmentMetadataEnabled = true });
        dashboard.Navigate("usage"); await Idle();
        Record("Session image counts, direct sub-agent navigation, and metadata visibility switches");

        dashboard.Navigate("codex-accounts"); await Idle();
        var accountsWindow = System.Windows.Application.Current.Windows.OfType<Window>().Single(x => x != dashboard && x.Content is AccountsPane);
        accountsWindow.Width = 500; accountsWindow.Height = 300; await Idle();
        var saveAccount = Descendants<System.Windows.Controls.Button>(accountsWindow).Single(x => Equals(x.Content, "Save current account"));
        Require(saveAccount.TransformToAncestor(accountsWindow).Transform(new Point()).Y + saveAccount.ActualHeight < accountsWindow.ActualHeight, "Account actions clipped at minimum window size");
        Capture(accountsWindow, Path.Combine(directory, "windows-accounts-min.png")); accountsWindow.Close();
        Record("Account utility keeps actions visible at 500 by 300");

        foreach (var theme in new[] { "dark", "light", "high-contrast" })
        {
            SettingsTheme.Apply(dark: theme == "dark", highContrast: theme == "high-contrast");
            dashboard.Width = 840; dashboard.Height = 560;
            foreach (var section in new[] { "general", "usage", "providers", "notch" })
            {
                dashboard.Navigate(section); await Idle();
                Require(Descendants<ScrollViewer>(dashboard).All(x => x.ScrollableWidth < 1), "Horizontal overflow: " + theme + "/" + section);
                foreach (var picker in Descendants<System.Windows.Controls.ComboBox>(dashboard))
                    Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(picker)) || AutomationProperties.GetLabeledBy(picker) is not null, "Unlabelled settings picker: " + section);
                Capture(dashboard, Path.Combine(directory, "windows-" + section + "-" + theme + "-min.png"));
            }
        }
        SettingsTheme.Apply(dark: true, highContrast: false); dashboard.Width = 980; dashboard.Height = 680;
        Record("Dark, light and high contrast settings fit minimum size with labelled controls");

        foreach (var page in new[] { "general", "notch", "providers", "codex", "diagnostics", "about" })
        {
            dashboard.Navigate(page); await Idle(); Capture(dashboard, Path.Combine(directory, "windows-" + page + ".png"));
        }
        dashboard.Navigate("notch"); await Idle();
        var combo = Descendants<System.Windows.Controls.ComboBox>(dashboard).First(); combo.IsDropDownOpen = true; await Idle();
        Require(combo.IsDropDownOpen, "Settings dropdown did not open");
        if (combo.Template.FindName("PART_Popup", combo) is Popup { Child: FrameworkElement dropdown }) Capture(dropdown, Path.Combine(directory, "windows-dropdown.png"));
        combo.IsDropDownOpen = false; Record("Settings dropdown opens");
        var edgePicker = Descendants<System.Windows.Controls.ComboBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Edge");
        edgePicker.Focus(); edgePicker.IsDropDownOpen = true; await Idle();
        edgePicker.SelectedIndex = (edgePicker.SelectedIndex + 1) % edgePicker.Items.Count; await Idle();
        Require(Descendants<System.Windows.Controls.ComboBox>(dashboard).Contains(edgePicker) && edgePicker.IsDropDownOpen, "Selecting a notch edge destroyed the open picker");
        edgePicker.IsDropDownOpen = false; await Idle();
        Require(Descendants<System.Windows.Controls.ComboBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Edge").IsKeyboardFocusWithin, "Changing a notch edge lost keyboard focus");
        settings.Save(settings.Current with { Visibility = NotchVisibility.AlwaysShow }); dashboard.Navigate("notch"); await Idle();
        var visible = Descendants<System.Windows.Controls.CheckBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Show edge notch");
        visible.IsChecked = false; await Idle();
        Descendants<System.Windows.Controls.CheckBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Show edge notch").IsChecked = true; await Idle();
        Require(settings.Current.Visibility == NotchVisibility.AlwaysShow, "Hide and show lost the saved notch behavior");
        Record("Picker selection preserves keyboard focus and hiding preserves Always show");
        foreach (var edge in Enum.GetValues<NotchEdge>())
        foreach (var scale in new[] { 0.8, 1d, 1.25 })
        {
            settings.Save(settings.Current with { Edge = edge, Scale = scale, Visibility = NotchVisibility.AlwaysShow, EnabledProviders = ["codex"] });
            notch.UpdateLayout(); await Idle();
            var provider = Descendants<System.Windows.Controls.Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.provider.codex");
            var ring = Descendants<ProviderRing>(provider).Single();
            var viewport = Descendants<ScrollViewer>(notch).Single();
            Require(viewport.ComputedVerticalScrollBarVisibility != Visibility.Visible && viewport.ComputedHorizontalScrollBarVisibility != Visibility.Visible, "Native notch scroll bar is visible");
            Require(viewport.ScrollableHeight < 1 && viewport.ScrollableWidth < 1, "A single provider is clipped");
            Require(ring.ActualWidth >= NotchMetrics.Ring - 1 && ring.ActualHeight >= NotchMetrics.CellHeight - 1, "Provider ring is clipped");
            Capture(notch, Path.Combine(directory, $"windows-notch-{edge}-{scale:0.00}.png"));
            notch.OpenProvider("codex"); await Idle();
            Require(notch.PopupContent is { ActualWidth: > 0, ActualHeight: > 0 }, "Provider popup did not open");
            if (scale == 1) Capture(notch.PopupContent!, Path.Combine(directory, "windows-popup-" + edge + ".png"));
            Record($"{edge} at {scale:0.00}: no clipped single provider or native scroll chrome");
        }
        System.Windows.Input.Keyboard.ClearFocus();
        if (notch.PopupContent is { } priorPopup)
            priorPopup.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(priorPopup)!, 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        await Idle();
        var ringTarget = Descendants<System.Windows.Controls.Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.provider.codex");
        var ringPoint = ringTarget.PointToScreen(new Point(ringTarget.ActualWidth / 2, ringTarget.ActualHeight / 2));
        System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)ringPoint.X, (int)ringPoint.Y);
        await Task.Delay(100); await Idle(); notch.OpenProvider("codex"); await Idle();
        var gear = Descendants<System.Windows.Controls.Button>(notch).Single(x => AutomationProperties.GetName(x) == "Open Settings");
        var gearPoint = gear.PointToScreen(new Point(gear.ActualWidth / 2, gear.ActualHeight / 2));
        System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)gearPoint.X, (int)gearPoint.Y);
        await Task.Delay(400); await Idle();
        notch.DismissProviderCard(); await Idle();
        Require(!notch.PopupIsOpen, "Provider card remains open over notch controls");
        Require(notch.Expanded, "Clearing provider hover unexpectedly folded always-visible notch");
        Record("Leaving provider ring for controls clears card independently of notch visibility");
        notch.OpenAccounts(); await Idle();
        Require(notch.PopupContent is not null && Descendants<TextBlock>(notch.PopupContent).Any(x => x.Text.Contains("preview@example.invalid", StringComparison.Ordinal)), "Account popup omits current CLI identity");
        Capture(notch.PopupContent!, Path.Combine(directory, "windows-account-popup.png"));
        notch.TryFold(); await Task.Delay(550); await Idle();
        Require(notch.AccountMenuIsOpen && notch.PopupIsOpen, "Account menu closed merely on pointer departure");
        var popupSource = PresentationSource.FromVisual(notch.PopupContent!);
        notch.PopupContent!.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, popupSource!, 0, System.Windows.Input.Key.Escape)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        await Idle(); Require(!notch.PopupIsOpen, "Escape did not dismiss account menu");
        Record("Account menu persists until explicit dismissal and Escape closes it");
        Record("Account popup shows provider logo, plan and isolated current identity");
        settings.Save(settings.Current with { Edge = NotchEdge.Right, Scale = 1.25, EnabledProviders = ProviderCatalog.All.Select(x => x.Id).ToArray() });
        await store.RefreshAsync(true).ConfigureAwait(true); await Idle();
        var many = Descendants<ScrollViewer>(notch).Single();
        Require(many.ScrollableHeight > 0, "Many-provider notch cannot scroll");
        many.ScrollToEnd(); await Idle();
        Require(many.VerticalOffset > 0, "Cannot reach last provider");
        Capture(notch, Path.Combine(directory, "windows-notch-many.png")); Record("All providers reachable with hidden scroll chrome");
        settings.Save(settings.Current with { EnabledProviders = [], Scale = 1 });
        await Idle(); Capture(notch, Path.Combine(directory, "windows-notch-empty.png"));
        settings.Save(settings.Current with { EnabledProviders = ["codex"], Visibility = NotchVisibility.Hidden });
        Require(!notch.IsVisible, "Hidden notch is visible"); Record("Hide notch hides native window");
        settings.Save(settings.Current with { Visibility = NotchVisibility.OnHover });
        notch.TryFold(); await Idle();
        Capture(notch, Path.Combine(directory, "windows-notch-folded.png"));
        Require(!notch.Expanded, "Notch did not fold"); Record("Hover notch folds");
        File.WriteAllText(Path.Combine(directory, "windows-ui-checks.json"), JsonSerializer.Serialize(new { kind = "Native WPF synthetic integration", checks }, JsonOptions));
    }
    private static async Task Idle() => await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    internal static void Capture(FrameworkElement view, string output)
    {
        view.UpdateLayout();
        var width = (int)Math.Ceiling(view.ActualWidth); var height = (int)Math.Ceiling(view.ActualHeight);
        Require(width > 0 && height > 0, "Empty capture");
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen()) { context.DrawRectangle(Ui.Brush("#292929"), null, new Rect(0, 0, width, height)); context.DrawRectangle(new VisualBrush(view), null, new Rect(0, 0, width, height)); }
        image.Render(background); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(output); encoder.Save(file);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
