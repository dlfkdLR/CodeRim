using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace CodeRim.Windows.Views;

internal static class SettingsTheme
{
    private const string FluentPrefix = "pack://application:,,,/PresentationFramework.Fluent;component/Themes/";
    private static ResourceDictionary? frameworkStyles;
    internal static Style FrameworkControlStyle(Type type)
    {
        var dictionary = type == typeof(System.Windows.Controls.Slider) ? CurrentFrameworkDictionary() : frameworkStyles
            ?? throw new InvalidOperationException("The Windows theme has not been initialized.");
        return (Style)dictionary[type == typeof(System.Windows.Controls.Slider) ? type : "Default" + type.Name + "Style"];
    }
    private static ResourceDictionary CurrentFrameworkDictionary() => Application.Current.Resources.MergedDictionaries
        .Single(x => x.Source?.OriginalString.StartsWith(FluentPrefix, StringComparison.Ordinal) == true);
    internal static bool IsDark { get; private set; }
    internal static bool IsHighContrast { get; private set; }
    internal static void Apply(bool? dark = null, bool? highContrast = null)
    {
        var followsSystem = dark is null;
        var contrast = highContrast ?? SystemParameters.HighContrast;
        IsHighContrast = contrast;
        if (dark is null)
        {
            try { dark = (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0; }
            catch (Exception e) when (e is System.Security.SecurityException or System.IO.IOException or UnauthorizedAccessException) { dark = false; }
        }
        IsDark = dark.Value;
        var resources = Application.Current.Resources;
        var source = new Uri(FluentPrefix + (followsSystem ? "Fluent.xaml" : IsDark ? "Fluent.Dark.xaml" : "Fluent.Light.xaml"));
        var dictionary = CurrentFrameworkDictionary();
        // StaticResource wrappers retain their original native style objects.
        // Preserve that dictionary once so native QA can verify their provenance.
        frameworkStyles ??= dictionary;
        if (dictionary.Source != source)
        {
            var index = resources.MergedDictionaries.IndexOf(dictionary);
            resources.MergedDictionaries[index] = new ResourceDictionary { Source = source };
        }
        // Alias the framework's brushes for custom charts/cards. Ordinary controls
        // keep the native styles, including disabled/selected/keyboard-focus states.
        Brush Native(string key, Brush fallback) => Application.Current.TryFindResource(key) as Brush ?? fallback;
        var accent = Native("AccentFillColorDefaultBrush", SystemColors.AccentColorBrush);
        var brushes = new Dictionary<string, Brush>
        {
            ["WindowBackground"] = Native("ApplicationBackgroundBrush", Solid(IsDark ? "#202020" : "#FAFAFA")),
            ["PanelBackground"] = Native("LayerFillColorDefaultBrush", Solid(IsDark ? "#2B2B2B" : "#F3F3F3")),
            ["ProviderPopupBackground"] = Native("ComboBoxDropDownBackground", Solid(IsDark ? "#2C2C2C" : "#F9F9F9")),
            ["CardBackground"] = Native("CardBackgroundFillColorDefaultBrush", Solid(IsDark ? "#2B2B2B" : "#FFFFFF")),
            ["LimitCardBackground"] = Native("CardBackgroundFillColorSecondaryBrush", Solid(IsDark ? "#2B2B2B" : "#F6F6F6")),
            ["ControlBackground"] = Native("ControlFillColorDefaultBrush", Solid(IsDark ? "#333333" : "#FFFFFF")),
            ["ControlHover"] = Native("SubtleFillColorSecondaryBrush", Solid(IsDark ? "#3B3B3B" : "#EAEAEA")),
            ["SelectedControl"] = Native("ControlFillColorInputActiveBrush", Solid(IsDark ? "#1F1F1F" : "#FFFFFF")),
            ["PrimaryText"] = Native("TextFillColorPrimaryBrush", Solid(IsDark ? "#FFFFFF" : "#202020")),
            ["SecondaryText"] = Native("TextFillColorSecondaryBrush", Solid(IsDark ? "#CFCFCF" : "#616161")),
            ["DividerBrush"] = Native("DividerStrokeColorDefaultBrush", Solid(IsDark ? "#444444" : "#D6D6D6")),
            ["AccentBrush"] = accent,
            ["AccentText"] = Native("TextOnAccentFillColorPrimaryBrush", Brushes.White),
            ["UsageAmple"] = Solid(IsDark ? "#6CCB5F" : "#0F7B0F"),
            ["UsageWatch"] = Solid(IsDark ? "#FCE100" : "#9D5D00"),
            ["UsageCritical"] = Solid(IsDark ? "#FFB900" : "#C42B1C"),
            ["AccentBorderBrush"] = Tint(accent, 0.4),
            ["AccentSubtleBrush"] = Tint(accent, 0.12),
            ["CaptionCloseHover"] = Solid("#E81123"),
            ["CaptionCloseHoverText"] = Brushes.White
        };
        if (contrast)
        {
            foreach (var name in new[] { "WindowBackground", "PanelBackground", "ProviderPopupBackground", "CardBackground", "LimitCardBackground", "ControlBackground", "SelectedControl", "ControlHover", "AccentSubtleBrush" }) brushes[name] = SystemColors.WindowBrush;
            brushes["PrimaryText"] = brushes["SecondaryText"] = brushes["DividerBrush"] = SystemColors.WindowTextBrush;
            brushes["AccentBrush"] = brushes["UsageAmple"] = brushes["UsageWatch"] = brushes["UsageCritical"] = brushes["AccentBorderBrush"] = brushes["CaptionCloseHover"] = SystemColors.HighlightBrush;
            brushes["AccentText"] = brushes["CaptionCloseHoverText"] = SystemColors.HighlightTextBrush;
        }
        foreach (var (key, brush) in brushes) resources[key] = brush;
    }
    private static SolidColorBrush Solid(string value) => new((Color)ColorConverter.ConvertFromString(value));
    private static Brush Tint(Brush brush, double opacity)
    {
        var tinted = brush.CloneCurrentValue(); tinted.Opacity *= opacity; tinted.Freeze(); return tinted;
    }
}
