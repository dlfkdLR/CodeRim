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
        image.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
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
