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
    public static TextBlock Text(string text, double size = 13, string color = "#EEEEF0", FontWeight? weight = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        if (color is "#EEEEF0" or "#FFFFFF") block.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryText");
        else if (color is "#A6A6AA" or "#B7B8BD" or "#808080" or "#9698A0" or "#98989D" or "#C6C6CA")
            block.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        else block.Foreground = Brush(color);
        return block;
    }
    public static StackPanel Stack(double gap = 0) => new() { Margin = new Thickness(gap) };
    public static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 4, 8, 4),
            MinHeight = 28, Cursor = System.Windows.Input.Cursors.Hand };
        button.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
        button.SetResourceReference(Control.BackgroundProperty, "ControlBackground");
        button.SetResourceReference(Control.BorderBrushProperty, "DividerBrush");
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
            HorizontalAlignment = HorizontalAlignment.Left };
        box.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new ChoiceLabel() });
        text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Control.Foreground))
        { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1) });
        box.ItemTemplate = new DataTemplate { VisualTree = text };
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is T value) changed(value); }; return box;
    }
    private sealed class ChoiceLabel : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value switch
        {
            CodeRim.Core.Domain.NotchVisibility.OnHover => "Show on hover",
            CodeRim.Core.Domain.NotchVisibility.AlwaysShow => "Always show",
            CodeRim.Core.Domain.NotchVisibility.Hidden => "Hidden",
            CodeRim.Core.Domain.RingColorMode.Usage => "Usage colours",
            CodeRim.Core.Domain.RingColorMode.Fixed => "Fixed colour",
            double scale => scale < 1 ? "Small" : scale > 1 ? "Large" : "Medium",
            "Relative" => "Countdown", "Absolute" => "Reset date",
            "Start" => "Above", "End" => "Below",
            0 => "Manual", 60 => "Automatic", 30 => "Every 30 seconds", 300 => "Every 5 minutes",
            _ => value?.ToString() ?? ""
        };
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
    public static CheckBox Toggle(string label, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 6, 0, 10) };
        box.SetResourceReference(Control.ForegroundProperty, "PrimaryText"); AutomationProperties.SetName(box, label);
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
