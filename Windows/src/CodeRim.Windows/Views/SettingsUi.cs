using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodeRim.Core.Domain;
using Button = System.Windows.Controls.Button;

namespace CodeRim.Windows.Views;

/// <summary>Mirrors SettingsMetrics and grouped row structure on macOS.</summary>
internal static class SettingsUi
{
    internal static void Resource(FrameworkElement element, DependencyProperty property, string key) => element.SetResourceReference(property, key);
    internal static Border Divider(double inset = 0)
    {
        var line = new Border { Height = 1, Margin = new Thickness(inset, 0, 0, 0) };
        Resource(line, Border.BackgroundProperty, "DividerBrush"); return line;
    }
    internal static StackPanel Section(string title, params UIElement[] rows)
    {
        var section = new StackPanel { Margin = new Thickness(18, 16, 18, 0) };
        var heading = Ui.Text(title, 12, "#A6A6AA", FontWeights.SemiBold); heading.Margin = new Thickness(14, 0, 14, 6);
        section.Children.Add(heading);
        var content = new StackPanel();
        foreach (var row in rows) { if (content.Children.Count > 0) content.Children.Add(Divider(14)); content.Children.Add(row); }
        var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = content };
        Resource(card, Border.BackgroundProperty, "CardBackground"); Resource(card, Border.BorderBrushProperty, "DividerBrush");
        section.Children.Add(card); return section;
    }
    internal static FrameworkElement Row(string title, FrameworkElement control, string? caption = null)
    {
        var grid = new Grid { Margin = new Thickness(14, 9, 14, 9), MinHeight = 22 };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var label = Ui.Text(title); label.Margin = new Thickness(0); labels.Children.Add(label);
        if (caption is not null) { var note = Ui.Text(caption, 11, "#A6A6AA"); note.Margin = new Thickness(0, 2, 0, 0); labels.Children.Add(note); }
        grid.Children.Add(labels); control.Margin = new Thickness(0); control.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, title); AutomationProperties.SetLabeledBy(control, label);
        Grid.SetColumn(control, 1); grid.Children.Add(control); return grid;
    }
    internal static FrameworkElement Value(string title, string value) => Row(title, Ui.Text(value, 13, "#A6A6AA"));
    internal static FrameworkElement Toggle(string title, bool value, Action<bool> changed, string? caption = null)
    {
        var toggle = Ui.Toggle(title, value, changed); toggle.Margin = new Thickness(14, 6, 14, 6);
        if (caption is not null)
        {
            var labels = new StackPanel(); var label = Ui.Text(title); label.Margin = new Thickness(0); labels.Children.Add(label);
            var note = Ui.Text(caption, 11, "#A6A6AA"); note.Margin = new Thickness(0, 2, 0, 0); labels.Children.Add(note); toggle.Content = labels;
        }
        AutomationProperties.SetName(toggle, title); return toggle;
    }
    internal static FrameworkElement Picker<T>(string title, IEnumerable<T> values, T selected, Action<T> changed)
    {
        var picker = Ui.Combo(values, selected, changed); picker.MinWidth = 100; picker.MaxWidth = 250; picker.MinHeight = 22; picker.Height = 24;
        picker.HorizontalAlignment = HorizontalAlignment.Right; return Row(title, picker);
    }
    internal static FrameworkElement Action(string title, Action action)
    {
        var button = Ui.Button(title, action); button.HorizontalAlignment = HorizontalAlignment.Left; button.Margin = new Thickness(14, 9, 14, 9); return button;
    }
    internal static TextBlock Note(string value)
    {
        var text = Ui.Text(value, 11, "#A6A6AA"); text.Margin = new Thickness(32, 6, 32, 0); return text;
    }
    internal static FrameworkElement Icon(string id)
    {
        var (color, path) = id switch
        {
            "general" => ("#808080", "M4,2 L6,2 7,0 9,0 10,2 12,2 14,4 14,6 16,7 16,9 14,10 14,12 12,14 10,14 9,16 7,16 6,14 4,14 2,12 2,10 0,9 0,7 2,6 2,4 Z M5,8 A3,3 0 1 0 11,8 A3,3 0 1 0 5,8"),
            "usage" => ("#007AFF", "M1,1 V15 H16 M4,12 V8 M8,12 V4 M12,12 V1"),
            "providers" => ("#5856D6", "M5,5 A3,3 0 1 0 11,5 A3,3 0 1 0 5,5 M2,15 Q2,10 8,10 Q14,10 14,15 Z"),
            "notch" => ("#00A5AD", "M1,2 H15 V14 H1 Z M4,3 H12 V6 H4 Z"),
            "diagnostics" => ("#E85C9D", "M1,3 H15 M1,8 H15 M1,13 H15 M5,1 V5 M11,6 V10 M6,11 V15"),
            _ => ("#808080", "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 M8,7 V12 M8,4 V5")
        };
        return new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5.5), Background = Ui.Brush(color),
            Child = new System.Windows.Shapes.Path { Data = Geometry.Parse(path), Stroke = Brushes.White, StrokeThickness = 1.3, Stretch = Stretch.Uniform, Margin = new Thickness(4) } };
    }
}
