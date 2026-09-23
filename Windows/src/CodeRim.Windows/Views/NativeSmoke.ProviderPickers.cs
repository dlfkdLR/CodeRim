using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task ProviderPickersRegression(DashboardWindow dashboard, DashboardStore store, AppSettingsStore settings, string directory)
    {
        var before = settings.Current; var checks = new List<string>();
        try
        {
            settings.Save(before with { EnabledProviders = ["codex", "claude"] });
            string? configured = null;
            var catalogue = new ProviderPickerWindow(dashboard, store, settings, id => settings.Save(settings.Current with { EnabledProviders = [..settings.Current.EnabledProviders, id] }), id => configured = id);
            try
            {
                catalogue.Show(); await Idle();
                var query = Descendants<TextBox>(catalogue).Single(x => AutomationProperties.GetAutomationId(x) == "providers.picker.search");
                Require(Descendants<ScrollViewer>(catalogue).Single(x => AutomationProperties.GetAutomationId(x) == "providers.picker.catalogue").ActualHeight > 200, "Provider cards have no usable viewport.");
                query.Text = " ChatGPT "; await Idle();
                Require(Descendants<Button>(catalogue).Any(x => AutomationProperties.GetName(x) == "Configure Codex"), "Description search does not find an already added provider.");
                query.Text = "no-provider-match-한글"; await Idle();
                Require(Descendants<StackPanel>(catalogue).Any(x => AutomationProperties.GetAutomationId(x) == "providers.picker.empty"), "Empty provider search has no feedback.");
                query.Text = "grok"; await Idle();
                Descendants<Button>(catalogue).Single(x => AutomationProperties.GetAutomationId(x) == "providers.picker.add.grok").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                Require(settings.Current.EnabledProviders.Count(x => x == "grok") == 1 && query.Text == "grok", "Adding a provider duplicated it or cleared the search.");
                Require(Descendants<TextBlock>(catalogue).Single(x => AutomationProperties.GetAutomationId(x) == "providers.picker.count").Text == "3 added", "Added count did not update.");
                query.Clear(); await Idle(); Capture(catalogue, Path.Combine(directory, "windows-provider-catalogue.png"));
                var cards = Descendants<ScrollViewer>(catalogue).Single(x => AutomationProperties.GetAutomationId(x) == "providers.picker.catalogue").Content as Grid;
                Require(cards?.ColumnDefinitions.Count == 3 && cards.Children.OfType<Border>().Any(x => Grid.GetColumn(x) == 2), "Provider catalogue does not have two card columns.");
                query.Text = "grok"; await Idle();
                Descendants<Button>(catalogue).Single(x => AutomationProperties.GetAutomationId(x) == "providers.picker.configure.grok").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                Require(configured == "grok" && !catalogue.IsVisible, "Added provider cannot open its settings.");
                checks.Add("Catalogue description search, empty state, two columns, stable Add, count and Settings");
            }
            finally { catalogue.Close(); }

            var picker = new UsageProviderPicker { ItemsSource = ProviderCatalog.All, SelectedValuePath = "Id", SelectedValue = "codex", DisplayMemberPath = "Name", Width = 142, Height = 34,
                Style = (Style)Application.Current.FindResource("UsageProviderPicker") };
            var fixture = new Window { Owner = dashboard, Width = 340, Height = 120, Content = picker };
            try
            {
                fixture.Show(); fixture.Activate(); await Idle(); picker.IsDropDownOpen = true; await Idle();
                var host = (Border)picker.Template.FindName("PART_ProviderContent", picker);
                Require(Math.Abs(host.ActualWidth - 272) < 1, "Provider popover width differs from the reference.");
                var query = Descendants<TextBox>(host).Single(x => AutomationProperties.GetAutomationId(x) == "usage.provider.search");
                Require(Descendants<Button>(host).Single(x => AutomationProperties.GetAutomationId(x) == "menu.provider.codex") is { } selected
                    && AutomationProperties.GetItemStatus(selected) == "Selected", "Current provider has no selected state.");
                query.Text = "codex"; await Idle();
                var clear = Descendants<Button>(host).Single(x => AutomationProperties.GetName(x) == "Clear search"); clear.Focus();
                clear.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(clear)!, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); await Idle();
                Require(query.Text.Length == 0 && picker.IsDropDownOpen && Equals(picker.SelectedValue, "codex"), "Keyboard Clear search selected a provider instead of clearing the query.");
                var dark = SettingsTheme.IsDark;
                try
                {
                    SettingsTheme.Apply(dark: false, highContrast: false); await Idle();
                    Require(Descendants<ProviderMark>(host).All(x => x.Foreground is System.Windows.Media.SolidColorBrush brush
                        && brush.Color == ((System.Windows.Media.SolidColorBrush)Application.Current.FindResource("PrimaryText")).Color), "Light mode provider marks retained a white foreground.");
                    Capture(host, Path.Combine(directory, "windows-usage-provider-popover-light.png"));
                }
                finally { SettingsTheme.Apply(dark: dark); }
                query.Text = "no-provider-match"; await Idle();
                Require(Descendants<StackPanel>(host).Any(x => AutomationProperties.GetAutomationId(x) == "usage.provider.empty"), "Provider search does not show an empty state.");
                query.Text = "claude"; await Idle();
                Capture(host, Path.Combine(directory, "windows-usage-provider-popover.png"));
                var source = PresentationSource.FromVisual(query) ?? throw new InvalidOperationException("Provider popup has no input source.");
                query.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); await Idle();
                Require(Equals(picker.SelectedValue, "claude") && !picker.IsDropDownOpen, "Enter did not select the filtered provider and close its popup.");
                picker.IsDropDownOpen = true; await Idle();
                query = Descendants<TextBox>(host).Single(x => AutomationProperties.GetAutomationId(x) == "usage.provider.search");
                query.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(query)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); await Idle();
                Require(!picker.IsDropDownOpen && Equals(picker.SelectedValue, "claude"), "Escape changed the provider selection.");
                picker.ItemsSource = ProviderCatalog.All.Where(x => x.Id is "codex" or "claude").ToArray(); picker.SelectedValue = "codex";
                picker.IsDropDownOpen = true; await Idle();
                var panel = (FrameworkElement)host.Child;
                foreach (var key in new[] { Key.Down, Key.End })
                {
                    panel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); await Idle();
                    Require(picker.IsDropDownOpen && Equals(picker.SelectedValue, "codex"), "Highlight navigation committed a provider before activation.");
                }
                panel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel)!, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); await Idle();
                Require(!picker.IsDropDownOpen && Equals(picker.SelectedValue, "claude"), "Space activated the old focused provider rather than the highlighted row.");
                checks.Add("Usage popover search, Clear search key activation, light glyphs, highlight navigation, Enter/Space and Escape");
            }
            finally { picker.IsDropDownOpen = false; fixture.Close(); }
        }
        finally { settings.Save(before); }
        File.WriteAllText(Path.Combine(directory, "windows-provider-pickers.json"), JsonSerializer.Serialize(new { completed = true, checks }));
    }
}
