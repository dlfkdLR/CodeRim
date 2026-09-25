using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CodeRim.Core.Domain;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace CodeRim.Windows.Views;

/// <summary>A searchable provider popover with the reference Mac's selection and keyboard behavior.</summary>
internal sealed class UsageProviderPicker : ComboBox
{
    public UsageProviderPicker() => IsTextSearchEnabled = false;
    private string? unavailableId;
    internal string? UnavailableId
    {
        get => unavailableId;
        set { if (unavailableId == value) return; unavailableId = value; if (IsDropDownOpen) BuildPopover(); }
    }
    private string query = "";
    private string? highlighted;
    private bool keyboardNavigation;
    private Border? host;
    private TextBox? search;
    private Button? clearSearchButton;
    private readonly StackPanel rows = new();
    private readonly Dictionary<string, Button> buttons = new(StringComparer.Ordinal);
    private ScrollViewer? scroll;
    private ProviderDefinition[] Matches => Items.OfType<ProviderDefinition>().Where(x => x.Id != unavailableId).Where(x => x.Name.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)
        || x.Id.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate(); host = GetTemplateChild("PART_ProviderContent") as Border;
        if (IsDropDownOpen) BuildPopover();
    }
    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        IsEnabled = Items.Count > 0;
        if (Items.Count == 0) IsDropDownOpen = false;
        else if (IsDropDownOpen) BuildPopover();
    }
    protected override void OnDropDownOpened(EventArgs e)
    {
        query = ""; highlighted = SelectedValue as string; keyboardNavigation = false;
        BuildPopover(); base.OnDropDownOpened(e);
        Dispatcher.BeginInvoke(() =>
        {
            if (search is not null) search.Focus();
            else if (host?.Child is FrameworkElement panel) panel.Focus();
        });
    }
    protected override void OnDropDownClosed(EventArgs e)
    {
        base.OnDropDownClosed(e); search = null;
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsDropDownOpen && e.Key is Key.Down or Key.Up or Key.Enter or Key.Space)
        { IsDropDownOpen = Items.Count > 0; e.Handled = true; return; }
        if (IsDropDownOpen)
        {
            if (clearSearchButton?.IsKeyboardFocusWithin == true && e.Key is Key.Enter or Key.Space)
            { clearSearchButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); e.Handled = true; return; }
            if (e.Key == Key.Escape) { IsDropDownOpen = false; Focus(); e.Handled = true; return; }
            if (e.Key is Key.Down or Key.Up)
            {
                var options = Matches;
                if (options.Length > 0)
                {
                    var index = Array.FindIndex(options, x => x.Id == highlighted);
                    if (index < 0) index = e.Key == Key.Down ? -1 : options.Length;
                    highlighted = options[Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, options.Length - 1)].Id;
                    keyboardNavigation = true; Highlight();
                    if (buttons.TryGetValue(highlighted, out var row)) { row.BringIntoView(); if (search?.IsKeyboardFocusWithin != true) row.Focus(); }
                }
                e.Handled = true; return;
            }
            if (e.Key is Key.Home or Key.End or Key.PageDown or Key.PageUp && search?.IsKeyboardFocusWithin != true)
            {
                var options = Matches;
                if (options.Length > 0)
                {
                    var index = Math.Max(0, Array.FindIndex(options, x => x.Id == highlighted));
                    index = e.Key switch { Key.Home => 0, Key.End => options.Length - 1, Key.PageDown => Math.Min(options.Length - 1, index + 6), _ => Math.Max(0, index - 6) };
                    highlighted = options[index].Id; keyboardNavigation = true; Highlight();
                    if (buttons.TryGetValue(highlighted, out var row)) { row.BringIntoView(); row.Focus(); }
                }
                e.Handled = true; return;
            }
            if (e.Key == Key.Enter || e.Key == Key.Space && search?.IsKeyboardFocusWithin != true)
            {
                if (highlighted is { } id && Matches.Any(x => x.Id == id)) Select(id);
                e.Handled = true; return;
            }
        }
        base.OnPreviewKeyDown(e);
    }
    private void Select(string id)
    {
        if (id == unavailableId || !Items.OfType<ProviderDefinition>().Any(x => x.Id == id)) return;
        SelectedValue = id; IsDropDownOpen = false; Focus();
    }
    private void BuildPopover()
    {
        if (host is null) return;
        var panel = new StackPanel { Focusable = true };
        panel.PreviewKeyDown += (_, e) => OnPreviewKeyDown(e);
        AutomationProperties.SetAutomationId(panel, "usage.provider.list");
        var header = new DockPanel { Margin = new Thickness(8, 4, 8, 10) };
        if (Items.Count > 6)
        {
            var count = Ui.Text(Items.Count.ToString(System.Globalization.CultureInfo.CurrentCulture), 11, "#A6A6AA"); count.Margin = new Thickness(0); DockPanel.SetDock(count, Dock.Right); header.Children.Add(count);
        }
        var title = Ui.Text("Switch provider", 13, "#A6A6AA", FontWeights.SemiBold); title.Margin = new Thickness(0); header.Children.Add(title); panel.Children.Add(header);
        search = null; clearSearchButton = null;
        if (Items.Count > 6 || query.Length > 0)
        {
            var searchRow = new DockPanel { Margin = new Thickness(2, 0, 2, 10) };
            search = new TextBox { Text = query, Padding = new Thickness(8), MinHeight = 32 };
            AutomationProperties.SetName(search, "Search providers"); AutomationProperties.SetAutomationId(search, "usage.provider.search");
            var clear = Ui.Button("×", () => { search?.Clear(); search?.Focus(); }); clear.Margin = new Thickness(4, 0, 0, 0); clear.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            clearSearchButton = clear;
            AutomationProperties.SetName(clear, "Clear search"); DockPanel.SetDock(clear, Dock.Right); searchRow.Children.Add(clear); searchRow.Children.Add(search); panel.Children.Add(searchRow);
            search.TextChanged += (_, _) => { query = search.Text; clear.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed; highlighted = Matches.FirstOrDefault()?.Id; BuildRows(); };
        }
        if (rows.Parent is ScrollViewer previous) previous.Content = null;
        scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 264 };
        panel.Children.Add(scroll); host.Child = panel; BuildRows();
    }
    private void BuildRows()
    {
        rows.Children.Clear(); buttons.Clear(); var matches = Matches;
        highlighted = matches.Any(x => x.Id == highlighted) ? highlighted : matches.FirstOrDefault()?.Id;
        if (matches.Length == 0)
        {
            var empty = new StackPanel { Height = 88, VerticalAlignment = VerticalAlignment.Center };
            var title = Ui.Text("No providers found", 13, weight: FontWeights.Medium); title.TextAlignment = TextAlignment.Center;
            var note = Ui.Text("Try another name.", 11, "#A6A6AA"); note.TextAlignment = TextAlignment.Center;
            empty.Children.Add(title); empty.Children.Add(note); AutomationProperties.SetAutomationId(empty, "usage.provider.empty"); rows.Children.Add(empty);
        }
        foreach (var option in matches)
        {
            var selected = Equals(SelectedValue, option.Id);
            var button = Ui.Button("", () => Select(option.Id)); button.Height = 40; button.Margin = new Thickness(2, 0, 2, 4); button.Padding = new Thickness(10, 0, 10, 0);
            button.Style = (Style)FindResource("UsageProviderOption"); button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.SetResourceReference(BackgroundProperty, selected ? "AccentSubtleBrush" : "WindowBackground");
            button.MouseEnter += (_, _) => { if (!selected) button.SetResourceReference(BackgroundProperty, "ControlHover"); };
            button.MouseLeave += (_, _) => button.SetResourceReference(BackgroundProperty, selected ? "AccentSubtleBrush" : "WindowBackground");
            var line = new DockPanel();
            var check = Ui.Text(selected ? "✓" : "", 11); check.Width = 14; check.Margin = new Thickness(8, 0, 0, 0); check.VerticalAlignment = VerticalAlignment.Center; check.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); DockPanel.SetDock(check, Dock.Right); line.Children.Add(check);
            var mark = new ProviderMark { ProviderId = option.Id, Width = 20, Height = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }; mark.SetResourceReference(ProviderMark.ForegroundProperty, "PrimaryText"); DockPanel.SetDock(mark, Dock.Left); line.Children.Add(mark);
            var text = Ui.Text(option.Name, 13, weight: selected ? FontWeights.SemiBold : FontWeights.Normal); text.Margin = new Thickness(0); text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis; text.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(text); button.Content = line;
            AutomationProperties.SetName(button, option.Name); AutomationProperties.SetAutomationId(button, "menu.provider." + option.Id); AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
            button.GotKeyboardFocus += (_, _) => { highlighted = option.Id; keyboardNavigation = true; Highlight(); };
            buttons[option.Id] = button; rows.Children.Add(button);
        }
        Highlight();
        Dispatcher.BeginInvoke(() => { if (highlighted is { } id && buttons.TryGetValue(id, out var button)) button.BringIntoView(); });
    }
    private void Highlight()
    {
        foreach (var (id, button) in buttons)
            if (keyboardNavigation && highlighted == id) button.SetResourceReference(BorderBrushProperty, "AccentBrush");
            else button.BorderBrush = Brushes.Transparent;
    }
}
