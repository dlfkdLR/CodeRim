using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;
using TextBox = System.Windows.Controls.TextBox;

namespace CodeRim.Windows.Views;

/// <summary>The fixed catalogue order and card metrics follow ProviderPickerView on Mac.</summary>
internal sealed class ProviderPickerWindow : Window
{
    internal ProviderPickerWindow(Window owner, DashboardStore store, AppSettingsStore settings, Action<string> add, Action<string> configure)
    {
        Title = "Add Providers"; Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "WindowBackground"); SetResourceReference(ForegroundProperty, "PrimaryText");
        var layout = new Grid { Width = 600, Height = 520, Margin = new Thickness(0) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new Border { Padding = new Thickness(24), Child = layout };
        // SwiftUI's 600 × 520 frame includes the 24-point content padding.
        layout.Width = 552; layout.Height = 472; Content = content;
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        heading.Children.Add(Ui.Text("Add Providers", 20, weight: FontWeights.SemiBold));
        heading.Children.Add(Ui.Text("Choose the tools you use to see their usage in the notch.", 13, "#A6A6AA")); layout.Children.Add(heading);
        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 16) }; Grid.SetRow(searchRow, 1); layout.Children.Add(searchRow);
        var search = new TextBox { Padding = new Thickness(10), MinHeight = 34 };
        AutomationProperties.SetName(search, "Search providers"); AutomationProperties.SetAutomationId(search, "providers.picker.search");
        var clear = Ui.Button("×", () => { search.Clear(); search.Focus(); }); clear.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(clear, "Clear search"); DockPanel.SetDock(clear, Dock.Right); searchRow.Children.Add(clear); searchRow.Children.Add(search);
        var cards = new Grid { Margin = new Thickness(1, 1, 1, 4) };
        cards.ColumnDefinitions.Add(new ColumnDefinition()); cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); cards.ColumnDefinitions.Add(new ColumnDefinition());
        var scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(scroll, "providers.picker.catalogue"); Grid.SetRow(scroll, 2); layout.Children.Add(scroll);
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) }; Grid.SetRow(footer, 3); layout.Children.Add(footer);
        var done = Ui.Button("Done", Close); done.IsDefault = true; done.Margin = new Thickness(0); DockPanel.SetDock(done, Dock.Right); footer.Children.Add(done);
        var count = Ui.Text("", 13, "#A6A6AA"); count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(0); footer.Children.Add(count);
        AutomationProperties.SetAutomationId(count, "providers.picker.count");
        var initial = settings.Current.EnabledProviders.ToHashSet(StringComparer.Ordinal);
        var order = ProviderCatalog.All.OrderBy(x => initial.Contains(x.Id)).ToArray();
        void Populate(string? focus = null)
        {
            var offset = scroll.VerticalOffset;
            cards.Children.Clear(); cards.RowDefinitions.Clear();
            count.Text = settings.Current.EnabledProviders.Length + " added";
            var term = search.Text.Trim(); clear.Visibility = search.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var matches = order.Where(x => x.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) || Description(x).Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            if (matches.Length == 0)
            {
                var empty = new StackPanel { Margin = new Thickness(0, 64, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
                empty.Children.Add(Ui.Text("No providers found", 15, weight: FontWeights.SemiBold)); empty.Children.Add(Ui.Text("Try another name.", 12, "#A6A6AA"));
                AutomationProperties.SetAutomationId(empty, "providers.picker.empty"); Grid.SetColumnSpan(empty, 3); cards.Children.Add(empty);
            }
            for (var i = 0; i < matches.Length; i++)
            {
                var provider = matches[i]; var added = settings.Current.EnabledProviders.Contains(provider.Id, StringComparer.Ordinal);
                if (i % 2 == 0) cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var inner = new StackPanel();
                var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                var mark = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 10, 0), Child = new ProviderMark { ProviderId = provider.Id, Margin = new Thickness(8) } };
                ((ProviderMark)mark.Child).SetResourceReference(ProviderMark.ForegroundProperty, "PrimaryText");
                mark.SetResourceReference(Border.BackgroundProperty, "ControlBackground"); DockPanel.SetDock(mark, Dock.Left); top.Children.Add(mark);
                var name = Ui.Text(provider.Name, 13, weight: FontWeights.SemiBold); name.VerticalAlignment = VerticalAlignment.Center; name.MaxHeight = 36; name.TextTrimming = TextTrimming.CharacterEllipsis; top.Children.Add(name); inner.Children.Add(top);
                var description = Ui.Text(Description(provider), 11, "#A6A6AA"); description.Height = 30; description.TextTrimming = TextTrimming.CharacterEllipsis; description.Margin = new Thickness(0, 0, 0, 10); inner.Children.Add(description);
                var row = new DockPanel(); var button = Ui.Button(added ? "Settings…" : "+ Add", () =>
                {
                    if (settings.Current.EnabledProviders.Contains(provider.Id, StringComparer.Ordinal)) { Close(); configure(provider.Id); }
                    else { add(provider.Id); Populate(provider.Id); }
                });
                button.FontSize = 11; button.MinHeight = 22; button.Margin = new Thickness(6, 0, 0, 0); button.Padding = new Thickness(7, 2, 7, 2);
                AutomationProperties.SetName(button, (added ? "Configure " : "Add ") + provider.Name); AutomationProperties.SetAutomationId(button, "providers.picker." + (added ? "configure." : "add.") + provider.Id);
                DockPanel.SetDock(button, Dock.Right); row.Children.Add(button);
                var available = store.AccountDisplay(provider.Id).Reading?.State == ReadingState.Ready;
                var state = Ui.Text(added ? "✓ Added" : available ? "Reading available" : "Connect after adding", 11, "#A6A6AA"); state.VerticalAlignment = VerticalAlignment.Center; state.Margin = new Thickness(0);
                if (added) state.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); row.Children.Add(state); inner.Children.Add(row);
                var card = new Border { Child = inner, Padding = new Thickness(14), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) };
                card.SetResourceReference(Border.BackgroundProperty, "CardBackground"); card.SetResourceReference(Border.BorderBrushProperty, added ? "AccentBorderBrush" : "DividerBrush");
                Grid.SetColumn(card, i % 2 * 2); Grid.SetRow(card, i / 2); cards.Children.Add(card);
                if (focus == provider.Id) Dispatcher.BeginInvoke(() => button.Focus());
            }
            scroll.ScrollToVerticalOffset(offset);
        }
        search.TextChanged += (_, _) => { scroll.ScrollToTop(); Populate(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Loaded += (_, _) => search.Focus(); Populate();
    }

    internal static string Description(ProviderDefinition provider) => provider.Id switch
    {
        "codex" => "Local tokens and ChatGPT account limits.", "claude" => "Local tokens and Claude Code limits.",
        "copilot" => "Chat, completions, and premium requests.", "cursor" => "Usage limits from the Cursor editor.",
        "grok" => "Usage limits from your Grok account.", "opencode" => "Usage from your OpenCode sign-in.",
        "commandcode" => "Credits for Command Code.", "glm" => "Your GLM Coding Plan allowance.",
        "ollama" => "Ollama Cloud usage and limits.", "gemini" => "Model allowances from Antigravity.",
        "ollama-local" => "Models running locally with Ollama.", _ => provider.Summary
    };
}
