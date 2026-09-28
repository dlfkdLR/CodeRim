using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodeRim.Core.Services;
using CodeRim.Core.Domain;

namespace CodeRim.Windows.Views;

internal sealed partial class UsagePane
{
    private SettingsUsageSection requestedSettingsSection = SettingsUsageSection.Analytics;
    private SettingsUsageSection EffectiveSettingsSection => requestedSettingsSection == SettingsUsageSection.Analytics && !settings.Current.AnalyticsEnabled
        ? SettingsUsageSection.Overview : requestedSettingsSection;
    private readonly WrapPanel settingsTabs = new() { Margin = new Thickness(24, 0, 24, 0) };
    private bool? tabsAnalyticsEnabled;
    private string? tabsProvider;
    private bool updatingTabs;

    private void SelectSettingsSection(SettingsUsageSection section)
    {
        requestedSettingsSection = section; destination = "overview"; history.Clear(); project = session = null; selectedModel = null;
        BuildControls(); Update();
    }
    private void BuildSettingsTabs()
    {
        settingsTabs.Children.Clear(); tabsAnalyticsEnabled = settings.Current.AnalyticsEnabled; tabsProvider = provider;
        settingsTabs.Visibility = destination == "overview" ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(settingsTabs, "usage.mode"); AutomationProperties.SetName(settingsTabs, "Usage view");
        foreach (var section in Enum.GetValues<SettingsUsageSection>())
        {
            if (section == SettingsUsageSection.Analytics && !settings.Current.AnalyticsEnabled) continue;
            var title = section switch { SettingsUsageSection.Overview => "Overview", SettingsUsageSection.Analytics => "Usage analytics",
                _ => (ProviderCatalog.Find(provider)?.Name ?? provider) + " Limits" };
            var tab = new RadioButton { Content = title, Tag = section, GroupName = "UsageMode", IsChecked = EffectiveSettingsSection == section,
                FontSize = 13, Padding = new Thickness(0, 11, 0, 11), Margin = new Thickness(0, 0, 24, 0), Cursor = System.Windows.Input.Cursors.Hand,
                Template = SettingsTabTemplate() };
            tab.SetResourceReference(Control.ForegroundProperty, "SecondaryText");
            AutomationProperties.SetAutomationId(tab, "settings.usage.tab." + section.ToString().ToLowerInvariant());
            AutomationProperties.SetName(tab, title);
            tab.Checked += (_, _) => { if (!updatingTabs) SelectSettingsSection(section); };
            settingsTabs.Children.Add(tab);
        }
    }
    private void RefreshSettingsTabs()
    {
        if (tabsAnalyticsEnabled != settings.Current.AnalyticsEnabled || tabsProvider != provider) BuildSettingsTabs();
        updatingTabs = true;
        try { foreach (var tab in settingsTabs.Children.OfType<RadioButton>()) tab.IsChecked = (SettingsUsageSection)tab.Tag == EffectiveSettingsSection; }
        finally { updatingTabs = false; }
    }
    private static ControlTemplate SettingsTabTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid)); root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 11, 0, 11)); root.AppendChild(text);
        var underline = new FrameworkElementFactory(typeof(Border), "Underline"); underline.SetValue(FrameworkElement.HeightProperty, 2d);
        underline.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Bottom); root.AppendChild(underline);
        var focus = new FrameworkElementFactory(typeof(Border), "Focus"); focus.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        focus.SetValue(Border.CornerRadiusProperty, new CornerRadius(3)); focus.SetValue(UIElement.IsHitTestVisibleProperty, false); root.AppendChild(focus);
        var template = new ControlTemplate(typeof(RadioButton)) { VisualTree = root };
        var selected = new Trigger { Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PrimaryText")));
        selected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        selected.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("PrimaryText"), "Underline")); template.Triggers.Add(selected);
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentBrush"), "Focus")); template.Triggers.Add(focused);
        return template;
    }
}
