using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace CodeRim.Windows.Views;

internal static class Ui
{
    public static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    public static TextBlock Text(string text, double size = 13, string color = "#EEEEF0", FontWeight? weight = null) => new()
    { Text = text, FontSize = size, Foreground = Brush(color), FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    public static StackPanel Stack(double gap = 0) => new() { Margin = new Thickness(gap) };
    public static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 4, 8, 4),
            MinHeight = 32, Background = Brush("#303238"), Foreground = Brushes.White, BorderBrush = Brush("#45474E"), Cursor = System.Windows.Input.Cursors.Hand };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action(); return button;
    }
    public static Button AsyncButton(string label, Func<Task> action)
    {
        var button = Button(label, () => { });
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action().ConfigureAwait(true); } catch (Exception e) when (e is not OutOfMemoryException) { System.Windows.MessageBox.Show("The action could not be completed. Your saved data has been retained. Please retry.", "CodeRim", MessageBoxButton.OK, MessageBoxImage.Error); } finally { button.IsEnabled = true; } };
        return button;
    }
    public static ComboBox Combo<T>(IEnumerable<T> values, T selected, Action<T> changed)
    {
        var box = new ComboBox { ItemsSource = values, SelectedItem = selected, MinWidth = 150, Margin = new Thickness(0, 5, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Left, Foreground = Brushes.White };
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Control.Foreground))
        { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1) });
        box.ItemTemplate = new DataTemplate { VisualTree = text };
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is T value) changed(value); }; return box;
    }
    public static CheckBox Toggle(string label, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 10) };
        box.Checked += (_, _) => changed(true); box.Unchecked += (_, _) => changed(false); return box;
    }
    public static void Section(Panel panel, string title)
    {
        panel.Children.Add(new Border { Height = 1, Background = Brush("#383A40"), Margin = new Thickness(0, 16, 0, 16) });
        panel.Children.Add(Text(title, 16, weight: FontWeights.SemiBold));
    }
    public static FrameworkElement Row(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(Text(label, color: "#B7B8BD")); var reading = Text(value, weight: FontWeights.SemiBold); reading.TextAlignment = TextAlignment.Right; reading.Margin = new Thickness(16, 0, 0, 6); Grid.SetColumn(reading, 1); grid.Children.Add(reading); return grid;
    }
}
