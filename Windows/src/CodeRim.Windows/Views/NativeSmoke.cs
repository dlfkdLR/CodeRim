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
internal static partial class NativeSmoke
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
        File.WriteAllText(Path.Combine(directory, "windows-native-host.json"), JsonSerializer.Serialize(new {
            os_architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            process_architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription
        }, JsonOptions));
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
        var pendingImport = new TaskCompletionSource<BrowserCookieJar>(TaskCreationOptions.RunContinuationsAsynchronously);
        var importSaved = false; CancellationToken pendingReadToken = default;
        vault.Save("cookie:qoder", "manual-fixture");
        var importWindow = BrowserConnections.CreateDialog(dashboard, "qoder", vault, () => importSaved = true,
            [new("Synthetic profile", "unused-fixture-directory")], (_, token) => { pendingReadToken = token; return pendingImport.Task; },
            (_, _) => Task.FromResult(new ProviderReading("qoder", ReadingState.Ready, [])));
        importWindow.Show(); await Idle();
        var importButton = Descendants<Button>(importWindow).Single(x => Equals(x.Content, "Import sign-in"));
        importButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        importWindow.Close();
        Require(pendingReadToken.IsCancellationRequested, "Closing import did not cancel its read");
        pendingImport.SetResult(new BrowserCookieJar([new("session", "fixture", ".qoder.com", "/", true, false, 0)], ["qoder.com", "qoder.com.cn"]));
        await Until(() => importButton.IsEnabled, "Cancelled import did not finish");
        Require(!importSaved && vault.Load("browser:qoder") is null && vault.Load("cookie:qoder") == "manual-fixture", "Closing browser import still changed credentials");
        var successfulImport = BrowserConnections.CreateDialog(dashboard, "qoder", vault, () => importSaved = true,
            [new("Synthetic profile", "unused-fixture-directory")], (_, _) => Task.FromResult(new BrowserCookieJar([new("session", "fixture", ".qoder.com", "/", true, false, 0)], ["qoder.com", "qoder.com.cn"])),
            (_, _) => Task.FromResult(new ProviderReading("qoder", ReadingState.Ready, [])));
        successfulImport.Show(); await Idle();
        Descendants<Button>(successfulImport).Single(x => Equals(x.Content, "Import sign-in")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle();
        Require(importSaved && vault.Load("browser:qoder") is not null && vault.Load("cookie:qoder") == "manual-fixture", "Browser import did not preserve manual fallback");
        var savedJar = vault.Load("browser:qoder");
        var rejectedImport = BrowserConnections.CreateDialog(dashboard, "qoder", vault, () => throw new InvalidOperationException("Rejected import was saved"),
            [new("Synthetic profile", "unused-fixture-directory")],
            (_, _) => Task.FromResult(new BrowserCookieJar([new("analytics", "not-auth", ".qoder.com", "/", true, false, 0)], ["qoder.com", "qoder.com.cn"])),
            (_, _) => Task.FromResult(new ProviderReading("qoder", ReadingState.NeedsAuth, [])));
        rejectedImport.Show(); await Idle();
        Descendants<Button>(rejectedImport).Single(x => Equals(x.Content, "Import sign-in")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle();
        Require(vault.Load("browser:qoder") == savedJar, "Failed provider verification overwrote the previous imported account");
        Require(Descendants<TextBlock>(rejectedImport).Single(x => AutomationProperties.GetAutomationId(x) == "browser-import.status").Text.Contains("Existing connections were kept", StringComparison.Ordinal), "Import failure was not visible");
        rejectedImport.Close();
        var verificationPending = new TaskCompletionSource<ProviderReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken verifyToken = default;
        var verifyingImport = BrowserConnections.CreateDialog(dashboard, "qoder", vault, () => throw new InvalidOperationException("Closed verification was saved"),
            [new("Synthetic profile", "unused-fixture-directory")],
            (_, _) => Task.FromResult(new BrowserCookieJar([new("session", "another-fixture", ".qoder.com", "/", true, false, 0)], ["qoder.com", "qoder.com.cn"])),
            (_, token) => { verifyToken = token; return verificationPending.Task; });
        verifyingImport.Show(); await Idle();
        var verifyButton = Descendants<Button>(verifyingImport).Single(x => Equals(x.Content, "Import sign-in"));
        verifyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
        Require(verifyToken.CanBeCanceled, "Verification did not receive its lifetime token");
        verifyingImport.Close(); Require(verifyToken.IsCancellationRequested, "Closing import did not cancel verification");
        verificationPending.SetResult(new ProviderReading("qoder", ReadingState.Ready, []));
        await Until(() => verifyButton.IsEnabled, "Closed verification did not finish");
        Require(vault.Load("browser:qoder") == savedJar, "Closed verification replaced the saved connection");
        foreach (var conflict in new[] { "created", "replaced", "deleted" })
        {
            if (conflict == "created") vault.Delete("browser:qoder"); else vault.Save("browser:qoder", savedJar!);
            var pending = new TaskCompletionSource<ProviderReading>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lateSaved = false;
            var late = BrowserConnections.CreateDialog(dashboard, "qoder", vault, () => lateSaved = true,
                [new("Synthetic profile", "fixture-only")],
                (_, _) => Task.FromResult(BrowserCookieJar.Parse(savedJar!, ["qoder.com", "qoder.com.cn"])),
                (_, _) => pending.Task);
            late.Show(); await Idle();
            var submit = Descendants<Button>(late).Single(button => Equals(button.Content, "Import sign-in"));
            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            if (conflict == "deleted") vault.Delete("browser:qoder"); else vault.Save("browser:qoder", "replacement-fixture");
            pending.SetResult(new ProviderReading("qoder", ReadingState.Ready, []));
            await Until(() => submit.IsEnabled, "Conflicting import did not finish.");
            Require(!lateSaved && vault.Load("browser:qoder") == (conflict == "deleted" ? null : "replacement-fixture"),
                "Late browser verification overwrote a changed connection.");
            Require(Descendants<TextBlock>(late).Any(text => text.Text == "The saved connection changed. Reopen this connection."),
                "Browser import conflict was not visible.");
            late.Close();
        }
        vault.Delete("browser:qoder"); vault.Delete("cookie:qoder");
        Record("Browser import dialog opens; cancel rejects late results; successful import preserves manual fallback");

        var fixturePath = @"C:\fixture-existing";
        {
            var cliDirectory = CliInstaller.Install(() => fixturePath, value => fixturePath = value);
            CliInstaller.Install(() => fixturePath, value => fixturePath = value);
            var pathEntries = fixturePath.Split(';');
            Require(pathEntries.Count(x => string.Equals(x, cliDirectory, StringComparison.OrdinalIgnoreCase)) == 1, "CLI installation duplicated PATH");
            Require(File.ReadAllText(Path.Combine(cliDirectory, "coderim.cmd")).Contains("CodeRimCLI.exe", StringComparison.Ordinal), "CLI wrapper is missing");
        }
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
                command.Skip(1), """{"session_id":"synthetic-unregistered","rate_limits":{"five_hour":{"used_percentage":53}}}""",
                timeout: TimeSpan.FromSeconds(45));
            Require(result.Contains("53%", StringComparison.Ordinal), "Installed Claude command did not read stdin");
            Record("Claude installation preserves settings, is idempotent, and executes its Windows command with stdin");
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousClaudeConfig); }

        settings.Save(settings.Current with { Visibility = NotchVisibility.AlwaysShow });
        settings.RevealNotch();
        Require(settings.Current.Visibility == NotchVisibility.AlwaysShow, "Tray reveal reset AlwaysShow");
        settings.Save(settings.Current with { Visibility = NotchVisibility.Hidden });
        settings.RevealNotch();
        Require(settings.Current.Visibility == NotchVisibility.AlwaysShow, "Tray reveal lost the last visible preference");
        settings.Save(settings.Current with { Visibility = NotchVisibility.OnHover });
        dashboard.Navigate("providers"); await Idle();
        var listPlan = Descendants<TextBlock>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "provider-list.codex");
        Require(listPlan.Text == "Preview account", "Provider list fixture was not loaded");
        store.InvalidateAccount("codex"); await Idle();
        Require(listPlan.Text != "Preview account", "Provider list kept stale plan after account invalidation");
        await store.RefreshProviderAsync("codex"); await Idle();
        Require(listPlan.Text == "Preview account" && Descendants<TextBlock>(dashboard).Contains(listPlan), "Provider list did not refresh its existing row");
        dashboard.Navigate("usage"); await Idle();
        Require(Descendants<Button>(dashboard).Any(x => AutomationProperties.GetAutomationId(x) == "usage.refresh"), "Usage header has no refresh action");
        var shortCard = NotchPopover.Create("codex", store, settings.Current, _ => { }, 140);
        shortCard.Measure(new Size(500, 1000)); shortCard.Arrange(new Rect(0, 0, 500, shortCard.DesiredSize.Height));
        Require(Descendants<ScrollViewer>(shortCard).Single().MaxHeight == 100, "Popover ignored the selected monitor viewport");
        Record("Tray reveal preserves visibility preference; provider list refreshes in place; Usage refresh and selected-monitor card viewport");
        dashboard.Navigate("codex"); await Idle();
        var planLabel = Descendants<TextBlock>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "provider.plan");
        var stateLabel = Descendants<TextBlock>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "provider.status");
        Require(planLabel.Text == "Preview account", "Provider plan fixture was not loaded");
        store.InvalidateAccount("codex"); await Idle();
        Require(planLabel.Text == "Unavailable" && stateLabel.Text == "Available", "Account invalidation kept old provider identity metadata");
        await store.RefreshProviderAsync("codex"); await Idle();
        Require(planLabel.Text == "Preview account" && stateLabel.Text == "Ready", "Provider metadata did not refresh in place");
        Require(Descendants<TextBlock>(dashboard).Contains(planLabel), "Provider refresh rebuilt the account card");
        Record("Provider plan and connection state follow account invalidation and refresh without rebuilding controls");
        dashboard.Navigate("usage"); await Idle();

        var activationReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationName = "CodeRim.Smoke.Activation." + Guid.NewGuid().ToString("N");
        using (var activationProbe = new InstanceActivation(activationName, () => activationReceived.TrySetResult()))
        {
            Require(await InstanceActivation.NotifyAsync(activationName), "Existing-instance activation could not connect");
            await activationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Record("Current-user-only activation IPC dispatches the fixed reopen action");

        var axRing = new ProviderRing { ProviderId = "codex", Reading = new("codex", ReadingState.Ready, [new("test", "Weekly", 32)], DateTimeOffset.Now) };
        Require(axRing.AccessibleReading() == "32% used", "Ring accessibility lost used percentage");
        axRing.Settings = settings.Current with { ShowRemaining = true };
        Require(axRing.AccessibleReading() == "68% remaining", "Ring accessibility lost remaining percentage");
        axRing.Reading = new("codex", ReadingState.NeedsAuth, []);
        Require(axRing.AccessibleReading().Contains("Sign in required", StringComparison.Ordinal), "Ring accessibility lost authentication state");
        dashboard.WindowState = WindowState.Minimized; dashboard.Navigate("usage"); await Idle();
        Require(dashboard.WindowState == WindowState.Normal, "Opening an existing settings window did not restore it");
        settings.Save(settings.Current with { CompletionSound = false, Visibility = NotchVisibility.OnHover });
        dashboard.Navigate("notch"); await Idle();
        Require(Descendants<ComboBox>(dashboard).Count(x => AutomationProperties.GetName(x) is "Finished" or "Blocked" && !x.IsEnabled) == 2, "Sound picker stayed enabled while sound was off");
        settings.Save(settings.Current with { CompletionSound = true }); dashboard.Navigate("notch"); await Idle();
        Require(Descendants<ComboBox>(dashboard).Count(x => AutomationProperties.GetName(x) is "Finished" or "Blocked" && x.IsEnabled) == 2, "Sound picker did not enable with sound");
        settings.Save(settings.Current with { CompletionSound = false }); dashboard.Navigate("usage"); await Idle();
        Record("Ring accessibility retains percentage and authentication meaning; reopen restores window; sound pickers respect the sound preference");

        Descendants<Button>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "usage.destination.activity").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle();
        var analyticsPeriod = Descendants<ComboBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Usage period");
        Require(Equals(analyticsPeriod.SelectedValue, "7d"), "Usage analytics did not default to the rolling seven-day range");
        var bucketButton = Descendants<Button>(dashboard).Last(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.tokens.", StringComparison.Ordinal));
        bucketButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
        var bucketDetails = Descendants<StackPanel>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "usage.bucket-details");
        var bucketText = string.Join("|", Descendants<TextBlock>(bucketDetails).Select(x => x.Text));
        Descendants<Button>(dashboard).First(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.model.", StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle();
        Descendants<UsagePane>(dashboard).Single().Back(); await Idle();
        Require(Descendants<StackPanel>(dashboard).Any(x => AutomationProperties.GetAutomationId(x) == "usage.bucket-details" && string.Join("|", Descendants<TextBlock>(x).Select(t => t.Text)) == bucketText), "Model drill-down lost the selected chart bucket on Back");
        Capture(dashboard, Path.Combine(directory, "windows-analytics-selected.png"));
        Descendants<UsagePane>(dashboard).Single().Back();
        dashboard.Navigate("usage"); await Idle();
        Record("Rolling analytics range, accessible bucket selection, model drill-down and Back preserve the selected interval");

        await FactorySessionRegression(vault, settings.Current);
        Record("Factory WorkOS profile refresh, DPAPI rotation, next-fetch stability, account replacement/removal and concurrent credential commits");
        await AmpSourceRegression(dashboard, settings, vault, directory);
        Record("Amp API/CLI/Web credential isolation, scoped browser verification, source picker and Web connection controls");
        await AntigravityRegression(dashboard, settings, vault, directory);
        Record("Antigravity native WMI identity, TCP owner before TLS, local quota/source isolation and real connector-to-WPF rendering");
        await NativeSourceRegression(dashboard, store, settings, vault, directory);
        Record("Amp/Windsurf source selection, volatile local scope, rejected API fallback, Antigravity alias, and Groq/Factory connection controls");
        await CopilotAuthenticationRegression(settings.Current, vault, directory);
        Record("Copilot DPAPI/environment/active hosts/CLI discovery, github.com contract and account replacement isolation");
        await GlmAuthenticationRegression(settings.Current, vault, directory);
        Record("GLM tool credentials, region and fixed-host isolation, explicit settings and account replacement");
        await CodebuffAuthenticationRegression(settings.Current, vault, directory);
        Record("Codebuff DPAPI/environment/local login precedence, optional subscription and account replacement isolation");
        await MoonshotRegionRegression(dashboard, settings, vault, directory);
        Record("Moonshot region switching preserves separate encrypted keys");
        await KimiConnectionRegression(dashboard, settings, vault, directory);
        Record("Kimi CLI and Web sources preserve credentials, quota priority and verified browser import");
        await StepFunConnectionRegression(dashboard, settings, vault, directory);
        Record("StepFun login, refresh and Auto-only browser import reach the ordinary store");
        await AlibabaConnectionRegression(dashboard, settings, vault, directory);
        Record("Alibaba CLI/Web/Auto preserves prior Web accounts, regional quota, volatile CLI data and actual native controls");
        await MiniMaxConnectionRegression(dashboard, settings, vault, directory);
        Record("MiniMax API/Web and Global/China credentials remain separate through native controls and browser import");
        await DeepSeekSourceRegression(settings, vault, directory);
        Record("DeepSeek API and platform credentials stay separate through native source switching");
        await AdditionalAuthenticationViews(settings, vault, directory);
        Record("Four native auth readers render connector results without synthetic preview");
        await AdditionalBrowserConnectionsRegression(dashboard, settings, vault, directory);
        Record("Five additional Firefox readers verify scoped login and render native quota");
        Record("Moonshot regional endpoints, DPAPI key isolation, environment aliases and source UI");
        await AnalyticsRegression(store, settings, directory);
        ActivityRegression(store);
        Record("Live Claude transcript completion and duplicate registry selection; provider-specific turn entry timing");
        Record("Narrow usage layout, proportional sub-dollar cost, cost gaps and Today/7D/30D totals");

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
        Require(!Descendants<System.Windows.Controls.TextBox>(dashboard).Any(), "List filter leaked into session detail");
        Require(Descendants<ListBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Settings sections").SelectedItem is ListBoxItem { Tag: "usage" }, "Session route left the wrong sidebar section selected");
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

        foreach (var accountProvider in new[] { "codex", "claude" })
        {
            dashboard.Navigate(accountProvider + "-accounts"); await Idle();
            var accountsWindow = System.Windows.Application.Current.Windows.OfType<Window>().Single(x => x != dashboard && x.Content is AccountsPane);
            accountsWindow.Width = accountsWindow.MinWidth; accountsWindow.Height = accountsWindow.MinHeight; await Idle();
            var saveAccount = Descendants<System.Windows.Controls.Button>(accountsWindow).Single(x => AutomationProperties.GetAutomationId(x) == "accounts.saveCurrent");
            var accountList = Descendants<ScrollViewer>(accountsWindow).Single(x => AutomationProperties.GetAutomationId(x) == "accounts.list");
            var footer = Descendants<StackPanel>(accountsWindow).Single(x => AutomationProperties.GetAutomationId(x) == "accounts.footer");
            var status = Descendants<TextBlock>(accountsWindow).Single(x => AutomationProperties.GetAutomationId(x) == "accounts.status");
            var accountPane = (AccountsPane)accountsWindow.Content;
            Require(accountPane.ActualWidth + accountPane.Margin.Left + accountPane.Margin.Right >= 499.5
                && accountPane.ActualHeight + accountPane.Margin.Top + accountPane.Margin.Bottom >= 299.5,
                "Account utility minimum must describe its client area, excluding native chrome");
            double Bottom(FrameworkElement element) => element.TransformToAncestor(accountPane).Transform(new Point()).Y + element.ActualHeight;
            Require(Bottom(accountList) <= saveAccount.TransformToAncestor(accountPane).Transform(new Point()).Y, "Account actions must follow the independently scrolling list");
            Require(Bottom(footer) <= accountPane.ActualHeight, "Account footer clipped at minimum window size");
            Require(Bottom(saveAccount) < accountPane.ActualHeight, "Account actions clipped at minimum window size");
            var originalSaveY = saveAccount.TransformToAncestor(accountPane).Transform(new Point()).Y;
            var savedList = (StackPanel)accountList.Content;
            for (var i = 0; i < 30; i++) savedList.Children.Add(Ui.Text("Synthetic saved account " + i));
            accountList.ScrollToEnd(); await Idle();
            Require(Math.Abs(saveAccount.TransformToAncestor(accountPane).Transform(new Point()).Y - originalSaveY) < 1, "Scrolling account list moved fixed actions");
            accountPane.ShowSignInProgress(true);
            status.Text = "The official CLI could not verify this account. Finish signing in through the CLI and retry."; await Idle();
            Require(Descendants<System.Windows.Controls.Button>(accountPane).Single(x => Equals(x.Content, "Cancel sign-in")).IsVisible, "Sign-in cancel action missing");
            Require(!Descendants<System.Windows.Controls.Button>(accountPane).Single(x => Equals(x.Content, "Refresh Accounts")).IsVisible, "Refresh should yield its action slot to Cancel during sign-in");
            Capture(accountsWindow, Path.Combine(directory, "windows-" + accountProvider + "-accounts-busy-min.png"));
            Require(accountList.ViewportHeight > 12 && accountList.ScrollableHeight > 0, $"Busy account footer consumed the scrolling list viewport: viewport={accountList.ViewportHeight}, scrollable={accountList.ScrollableHeight}, content={accountPane.ActualHeight}, footer={footer.ActualHeight}");
            Require(Bottom(footer) <= accountPane.ActualHeight, "Account error status clipped footer actions");
            Capture(accountsWindow, Path.Combine(directory, "windows-" + accountProvider + "-accounts-min.png"));
            if (accountProvider == "codex") Capture(accountsWindow, Path.Combine(directory, "windows-accounts-min.png"));
            accountsWindow.Close();
        }
        Record("Codex and Claude account utilities keep footer actions and error status below independently scrolling lists at 500 by 300 client area");

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
            Require(Descendants<TextBlock>(notch.PopupContent!).Count(x => x.Text.Contains("2 resets", StringComparison.Ordinal)) == 1,
                "Reset credit balance was duplicated in the popup");
            if (scale == 1) Capture(notch.PopupContent!, Path.Combine(directory, "windows-popup-" + edge + ".png"));
            Record($"{edge} at {scale:0.00}: no clipped single provider or native scroll chrome");
        }
        var originalCreditReading = store.Readings["codex"];
        try
        {
            foreach (var creditText in new[] { "Unlimited resets", "Reset count unavailable" })
            {
                store.Readings["codex"] = originalCreditReading with { Windows =
                    [new("rate-limit-reset-credits", "Reset credits", Unit: "resets", DisplayValue: creditText)] };
                notch.OpenProvider("codex"); await Idle();
                Require(Descendants<TextBlock>(notch.PopupContent!).Any(x => x.Text == creditText), "Non-numeric reset credit status disappeared");
            }
        }
        finally { store.Readings["codex"] = originalCreditReading; }
        notch.OpenProvider("codex"); await Idle();
        Record("Reset credit balance is shown once and unlimited/unavailable states remain visible");
        System.Windows.Input.Keyboard.ClearFocus();
        // This is pre-hover cleanup. A popup retains its Child after closing, so
        // PopupContent alone does not imply an attached presentation source.
        // Use the stable notch window's real Escape handler; the account-popup
        // Escape route is independently exercised below.
        var cleanupSource = PresentationSource.FromVisual(notch)
            ?? throw new InvalidOperationException("The visible notch has no presentation source.");
        notch.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, cleanupSource, 0, System.Windows.Input.Key.Escape)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        await Idle(); Require(!notch.PopupIsOpen, "Escape cleanup did not close the provider popup");
        var ringTarget = Descendants<System.Windows.Controls.Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.provider.codex");
        var ringPoint = ringTarget.PointToScreen(new Point(ringTarget.ActualWidth / 2, ringTarget.ActualHeight / 2));
        System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)ringPoint.X, (int)ringPoint.Y);
        await Task.Delay(100); await Idle(); notch.OpenProvider("codex"); await Idle();
        var gear = Descendants<System.Windows.Controls.Button>(notch).Single(x => AutomationProperties.GetName(x) == "Open Settings");
        var gearPoint = gear.PointToScreen(new Point(gear.ActualWidth / 2, gear.ActualHeight / 2));
        var pointerMoved = MoveCursor((int)gearPoint.X, (int)gearPoint.Y);
        var pointerError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        await Task.Delay(400); await Idle();
        var pointer = System.Windows.Forms.Cursor.Position;
        var targetHandle = WindowAtPoint(new PointerPoint { X = pointer.X, Y = pointer.Y });
        var notchHandle = new System.Windows.Interop.WindowInteropHelper(notch).Handle;
        var reachedNotch = pointerMoved && Math.Abs(pointer.X - gearPoint.X) <= 1 && Math.Abs(pointer.Y - gearPoint.Y) <= 1 && targetHandle == notchHandle;
        File.WriteAllText(Path.Combine(directory, "windows-pointer-input.json"), JsonSerializer.Serialize(new {
            requested = new { gearPoint.X, gearPoint.Y }, observed = new { pointer.X, pointer.Y },
            target = PointerOwner(targetHandle), moveSucceeded = pointerMoved, moveWin32Error = pointerError, hitNotchWindow = targetHandle == notchHandle,
            gear.IsMouseOver, realHoverVerified = reachedNotch && gear.IsMouseOver,
            outcome = reachedNotch ? gear.IsMouseOver ? "PASS" : "FAIL" : "INCONCLUSIVE"
        }, JsonOptions));
        Require(!reachedNotch || gear.IsMouseOver, "Pointer reached the notch window but the gear did not receive hover");
        if (!gear.IsMouseOver)
        {
            // Only native cursor/hit-window evidence can classify this input limitation.
            // Exercise the handler separately when another desktop/window intercepts input.
            Record("Real pointer hover inconclusive; see windows-pointer-input.json. Control handler uses a routed MouseEnter event");
            gear.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
            await Idle();
        }
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
        settings.Save(settings.Current with { CompletionSound = false, PeekOnCompletion = true });
        var attentionEvents = 0;
        void Attention(SessionActivity _) => attentionEvents++;
        store.SessionAttentionRequested += Attention;
        var activity = new SessionActivity("preview-transition", "claude", "Preview", "busy", DateTimeOffset.Now);
        store.UpdateSessionActivity([activity]);
        store.UpdateSessionActivity([activity with { State = "waiting" }]); await Idle();
        Require(attentionEvents == 1 && notch.Expanded, "Blocked session did not peek when sounds are off");
        store.UpdateSessionActivity([activity with { State = "waiting" }]);
        Require(attentionEvents == 1, "Unchanged blocked state repeated attention");
        store.UpdateSessionActivity([activity]);
        store.UpdateSessionActivity([activity with { State = "idle" }]);
        Require(attentionEvents == 2, "Finished session did not request attention");
        store.SessionAttentionRequested -= Attention;
        using var ownProcess = System.Diagnostics.Process.GetCurrentProcess();
        var previousFocusConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var focusConfig = Path.Combine(CompanionFile.DataDirectory, "focus-fixture");
        var focusSessions = Path.Combine(focusConfig, "sessions"); Directory.CreateDirectory(focusSessions);
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", focusConfig);
            var record = Path.Combine(focusSessions, "fixture.json");
            File.WriteAllText(record, JsonSerializer.Serialize(new { pid = ownProcess.Id, sessionId = "focus-fixture", status = "busy" }));
            Require(ClaudeSessions.Read().Single() is { ProcessId: null, ProcessStartedAt: null }, "Missing original start time granted process activation");
            File.WriteAllText(record, JsonSerializer.Serialize(new { pid = ownProcess.Id, sessionId = "focus-fixture", status = "busy",
                startedAt = new DateTimeOffset(ownProcess.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() }));
            Require(ClaudeSessions.Read().Single().ProcessId == ownProcess.Id, "Verified session process identity was lost");
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousFocusConfig); }
        var ownSession = activity with { ProcessId = ownProcess.Id, ProcessStartedAt = new DateTimeOffset(ownProcess.StartTime.ToUniversalTime()) };
        Require(SessionFocus.FindOwningWindow(ownSession) != IntPtr.Zero, "Session window lookup failed for the synthetic app");
        Require(SessionFocus.FindOwningWindow(ownSession with { ProcessStartedAt = ownSession.ProcessStartedAt.GetValueOrDefault().AddMinutes(-1) }) == IntPtr.Zero, "Reused process identity was accepted");
        Record("Session registry activation requires original process identity and rejects reused processes");
        notch.Peek(activity with { Provider = "codex" }); await Idle();
        var attentionButton = Descendants<System.Windows.Controls.Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.provider.codex");
        attentionButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
        Require(Descendants<ListBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Settings sections").SelectedItem is ListBoxItem { Tag: "usage" }, "Unavailable session target did not open local sessions");
        Record("Session window discovery, process-reuse rejection, and unavailable-target fallback");
        Record("Blocked and finished sessions peek independently of sound, without duplicate alerts");
        File.WriteAllText(Path.Combine(directory, "windows-ui-checks.json"), JsonSerializer.Serialize(new { kind = "Native WPF synthetic integration", checks }, JsonOptions));
    }
    private static async Task Until(Func<bool> condition, string failure)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline) { await Task.Delay(10); await Idle(); }
        Require(condition(), failure);
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
