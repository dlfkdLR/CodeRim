using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace CodeRim.Windows.Views;

internal static class SettingsTheme
{
    internal static bool IsDark { get; private set; }
    internal static void Apply(bool? dark = null, bool? highContrast = null)
    {
        var contrast = highContrast ?? SystemParameters.HighContrast;
        if (dark is null)
        {
            try { dark = (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0; }
            catch (Exception e) when (e is System.Security.SecurityException or System.IO.IOException or UnauthorizedAccessException) { dark = false; }
        }
        IsDark = dark.Value;
        var colors = new Dictionary<string, Color>
        {
            ["WindowBackground"] = Color(IsDark ? "#202020" : "#FFFFFF"),
            ["PanelBackground"] = Color(IsDark ? "#292929" : "#F0F0F0"),
            ["CardBackground"] = Color(IsDark ? "#202020" : "#FFFFFF"),
            ["ControlBackground"] = Color(IsDark ? "#303030" : "#F3F3F3"),
            ["ControlHover"] = Color(IsDark ? "#414141" : "#E5E5E5"),
            ["SelectedControl"] = Color(IsDark ? "#4D4D4D" : "#FFFFFF"),
            ["PrimaryText"] = Color(IsDark ? "#E6E6E6" : "#262626"),
            ["SecondaryText"] = Color(IsDark ? "#9E9E9E" : "#707070"),
            ["DividerBrush"] = Color(IsDark ? "#383838" : "#E3E3E3"),
            ["AccentBrush"] = Color(IsDark ? "#0A84FF" : "#007AFF"),
            ["AccentText"] = Colors.White
        };
        if (contrast)
        {
            foreach (var name in new[] { "WindowBackground", "PanelBackground", "CardBackground", "ControlBackground", "SelectedControl" }) colors[name] = SystemColors.WindowColor;
            colors["PrimaryText"] = colors["SecondaryText"] = SystemColors.WindowTextColor;
            colors["DividerBrush"] = SystemColors.WindowTextColor;
            colors["AccentBrush"] = SystemColors.HighlightColor;
            colors["AccentText"] = SystemColors.HighlightTextColor;
            colors["ControlHover"] = SystemColors.WindowColor;
        }
        foreach (var (key, color) in colors) Application.Current.Resources[key] = new SolidColorBrush(color);
    }
    private static Color Color(string value) => (Color)ColorConverter.ConvertFromString(value);
}
