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
        Require(store.Synthetic, "Smoke must use synthetic data");
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
        checks.Add("Windows private-file ACL, atomic replacement, and user DPAPI round trip");
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
            checks.Add("Claude installation preserves settings, is idempotent, and executes its Windows command with stdin");
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousClaudeConfig); }

        var glyphGrid = new WrapPanel { Width = 720, Background = Ui.Brush("#202020") };
        foreach (var provider in ProviderCatalog.All)
        {
            Require(ProviderMark.HasGlyph(provider.Id), "Provider logo is missing: " + provider.Id);
            var tile = new StackPanel { Width = 120, Height = 70, HorizontalAlignment = HorizontalAlignment.Center };
            tile.Children.Add(new ProviderMark { ProviderId = provider.Id, Width = 28, Height = 28, Margin = new Thickness(0, 6, 0, 4) });
            tile.Children.Add(Ui.Text(provider.Name, 10)); glyphGrid.Children.Add(tile);
        }
        glyphGrid.Measure(new Size(720, double.PositiveInfinity)); glyphGrid.Arrange(new Rect(glyphGrid.DesiredSize));
        Capture(glyphGrid, Path.Combine(directory, "windows-provider-logos.png")); checks.Add("Every provider logo loads and renders");
        await Idle(); Capture(dashboard, output); checks.Add("Usage window renders");
        var selector = Descendants<System.Windows.Controls.ComboBox>(dashboard).First();
        selector.Focus(); var original = selector;
        await store.RefreshAsync(true).ConfigureAwait(true); await Idle();
        Require(Descendants<System.Windows.Controls.ComboBox>(dashboard).Contains(original), "Refresh replaced usage selector");
        Require(original.IsKeyboardFocusWithin, "Refresh stole usage keyboard focus"); checks.Add("Refresh preserves usage selector and keyboard focus");
        foreach (var page in new[] { "general", "notch", "providers", "codex", "diagnostics", "about" })
        {
            dashboard.Navigate(page); await Idle(); Capture(dashboard, Path.Combine(directory, "windows-" + page + ".png"));
        }
        dashboard.Navigate("notch"); await Idle();
        var combo = Descendants<System.Windows.Controls.ComboBox>(dashboard).First(); combo.IsDropDownOpen = true; await Idle();
        Require(combo.IsDropDownOpen, "Settings dropdown did not open");
        if (combo.Template.FindName("PART_Popup", combo) is Popup { Child: FrameworkElement dropdown }) Capture(dropdown, Path.Combine(directory, "windows-dropdown.png"));
        combo.IsDropDownOpen = false; checks.Add("Settings dropdown opens");
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
            checks.Add($"{edge} at {scale:0.00}: no clipped single provider or native scroll chrome");
        }
        settings.Save(settings.Current with { Edge = NotchEdge.Right, Scale = 1.25, EnabledProviders = ProviderCatalog.All.Select(x => x.Id).ToArray() });
        await store.RefreshAsync(true).ConfigureAwait(true); await Idle();
        var many = Descendants<ScrollViewer>(notch).Single();
        Require(many.ScrollableHeight > 0, "Many-provider notch cannot scroll");
        many.ScrollToEnd(); await Idle();
        Require(many.VerticalOffset > 0, "Cannot reach last provider");
        Capture(notch, Path.Combine(directory, "windows-notch-many.png")); checks.Add("All providers reachable with hidden scroll chrome");
        settings.Save(settings.Current with { EnabledProviders = [], Scale = 1 });
        await Idle(); Capture(notch, Path.Combine(directory, "windows-notch-empty.png"));
        settings.Save(settings.Current with { EnabledProviders = ["codex"], Visibility = NotchVisibility.Hidden });
        Require(!notch.IsVisible, "Hidden notch is visible"); checks.Add("Hide notch hides native window");
        settings.Save(settings.Current with { Visibility = NotchVisibility.OnHover });
        notch.TryFold(); await Idle();
        Capture(notch, Path.Combine(directory, "windows-notch-folded.png"));
        Require(!notch.Expanded, "Notch did not fold"); checks.Add("Hover notch folds");
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
