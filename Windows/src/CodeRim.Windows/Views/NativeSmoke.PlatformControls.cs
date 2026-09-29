using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static readonly string[] PlatformChoices = ["First", "Second"];
    private static readonly bool[] PlatformCheckboxChanges = [true, false];
    private static async Task PlatformControlsRegression(DashboardWindow dashboard, string directory)
    {
        var checks = new List<string>();
        var wasDark = SettingsTheme.IsDark; var wasContrast = SettingsTheme.IsHighContrast;
        Window? fixture = null;
        try
        {
            Require(AppContext.TryGetSwitch("Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop", out var disabled) && disabled,
                "Fluent controls must not add a backdrop to transparent notch windows.");
            var clicked = 0; var changed = new List<bool>(); var selected = "First";
            var action = Ui.Button("Native action", () => clicked++);
            var checkbox = Ui.Toggle("Native checkbox", false, value => changed.Add(value));
            var captioned = (CheckBox)SettingsUi.Toggle("Native caption", false, _ => { }, "Secondary caption");
            var glyphAction = Ui.RefreshButton("Native glyph", () => Task.CompletedTask);
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50, SmallChange = 5 };
            var scrollbar = new ScrollBar { Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 100, Value = 50 };
            var combo = Ui.Combo(PlatformChoices, selected, value => selected = value);
            var text = new TextBox { Text = "Editable settings" };
            var password = new PasswordBox { Password = "synthetic-only" };
            var item = new ListBoxItem { Content = "Native selection" };
            var disclosure = new Expander { Header = "About these limits", Content = "Details", Style = (Style)Application.Current.FindResource("UsageLimitsDisclosure") };
            Control[] controls = [action, checkbox, captioned, glyphAction, slider, scrollbar, combo, text, password, item, disclosure];
            var panel = new StackPanel { Margin = new Thickness(16) };
            foreach (var control in controls) { control.Margin = new Thickness(0, 4, 0, 4); panel.Children.Add(control); }
            fixture = new Window { Owner = dashboard, Title = "Synthetic Windows controls", Width = 380, Height = 650, Content = panel };
            fixture.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
            fixture.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
            fixture.Show(); await Idle();
            File.WriteAllText(Path.Combine(directory, "windows-platform-style-diagnostics.json"), JsonSerializer.Serialize(controls.Select(control => new
            {
                type = control.GetType().Name, style_present = control.Style is not null, base_present = control.Style?.BasedOn is not null,
                direct_setters = control.Style?.Setters.OfType<Setter>().Select(x => x.Property.Name).ToArray(),
                application_style_matches = ReferenceEquals(control.Style, Application.Current.TryFindResource(control.GetType())),
                framework_style_matches = ReferenceEquals(control.GetType() == typeof(Slider) ? control.Style : control.Style?.BasedOn, SettingsTheme.FrameworkControlStyle(control.GetType())),
                template_present = control.Template is not null
            }), JsonOptions));
            Require(action.HorizontalAlignment == HorizontalAlignment.Stretch && Math.Abs(action.ActualWidth - panel.ActualWidth) < 1,
                "The native button theme narrowed a full-width action target.");
            foreach (var control in controls)
            {
                var style = control.Style;
                var frameworkStyle = SettingsTheme.FrameworkControlStyle(control.GetType());
                Require(control is Slider ? ReferenceEquals(style, frameworkStyle)
                    : style?.BasedOn is { } && ReferenceEquals(style.BasedOn, frameworkStyle) && !style.Setters.OfType<Setter>().Any(x => x.Property == Control.TemplateProperty),
                    "Settings overrides the native control template: " + control.GetType().Name);
                var probe = (Control)Activator.CreateInstance(control.GetType())!;
                if (control is ScrollBar actualBar && probe is ScrollBar expectedBar) expectedBar.Orientation = actualBar.Orientation;
                probe.Style = frameworkStyle; probe.ApplyTemplate();
                Require(ReferenceEquals(control.Template, probe.Template), "Settings lost the inherited Fluent template: " + control.GetType().Name);
            }
            Require(checkbox.Template.FindName("Switch", checkbox) is null && !Motion.GetFeedback(checkbox),
                "The Windows checkbox still uses a custom sliding switch.");
            action.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            checkbox.IsChecked = true; checkbox.IsChecked = false; combo.SelectedItem = "Second";
            Require(clicked == 1 && changed.SequenceEqual(PlatformCheckboxChanges) && selected == "Second",
                "Native control replacement disconnected action/setting/selection callbacks.");
            slider.Focus();
            slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(slider)!, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
            Require(slider.Value == 55, "The native slider lost keyboard increments.");
            disclosure.IsExpanded = true; await Idle();
            Require(disclosure.IsExpanded && Descendants<TextBlock>(disclosure).Any(x => x.Text == "Details" && x.IsVisible),
                "The native disclosure cannot reveal its content.");
            checks.Add("Settings inherit Fluent Button, CheckBox, Slider, ScrollBar, ComboBox, TextBox, PasswordBox, ListBoxItem and Expander templates");
            checks.Add("Native checkbox, action, selection, slider keyboard and disclosure preserve their callbacks and behavior");

            foreach (var dark in new[] { false, true })
            {
                SettingsTheme.Apply(dark, highContrast: false); await Idle();
                Require(ReferenceEquals(Application.Current.FindResource("AccentBrush"), Application.Current.FindResource("AccentFillColorDefaultBrush"))
                    && ReferenceEquals(Application.Current.FindResource("PrimaryText"), Application.Current.FindResource("TextFillColorPrimaryBrush")),
                    "Settings/chart colors diverged from Windows Fluent resources.");
                Require(Application.Current.FindResource("ProviderPopupBackground") is SolidColorBrush popupBrush && popupBrush.Color.A == 255 && popupBrush.Opacity == 1,
                    "Provider popup background is transparent in the Windows theme.");
                Require(text.Text == "Editable settings" && combo.SelectedItem as string == "Second" && checkbox.IsChecked == false,
                    "Theme changes discarded settings edits or control selection.");
                checkbox.IsEnabled = false; action.IsEnabled = false; combo.IsEnabled = false; captioned.IsEnabled = false; glyphAction.IsEnabled = false; await Idle();
                var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(checkbox);
                Require(peer is not null && !peer.IsEnabled(), "The disabled native checkbox reports an enabled accessibility state.");
                var presenter = (ContentPresenter)combo.Template.FindName("PART_ContentPresenter", combo);
                var selectedText = Descendants<TextBlock>(presenter).Single(x => x.Text == "Second");
                Require(ReferenceEquals(selectedText.Foreground, System.Windows.Documents.TextElement.GetForeground(presenter)),
                    "The selector label overrides the native disabled foreground.");
                var captions = Descendants<TextBlock>(captioned).Where(x => x.Text is "Native caption" or "Secondary caption").ToArray();
                Require(captions.Length == 2 && captions.All(x => ReferenceEquals(x.Foreground, captioned.Foreground)),
                    "Captioned checkbox text overrides the native disabled foreground.");
                var glyph = (System.Windows.Shapes.Path)glyphAction.Content;
                var glyphPresenter = Descendants<ContentPresenter>(glyphAction).Single(x => ReferenceEquals(x.Content, glyph));
                Require(ReferenceEquals(glyph.Stroke, System.Windows.Documents.TextElement.GetForeground(glyphPresenter)),
                    "The action glyph overrides the native disabled foreground.");
                Capture(panel, Path.Combine(directory, "windows-platform-controls-" + (dark ? "dark" : "light") + ".png"));
                checkbox.IsEnabled = true; action.IsEnabled = true; combo.IsEnabled = true; captioned.IsEnabled = true; glyphAction.IsEnabled = true; await Idle();
                Require(ReferenceEquals(captions.Single(x => x.Text == "Native caption").Foreground, Application.Current.FindResource("PrimaryText"))
                    && ReferenceEquals(captions.Single(x => x.Text == "Secondary caption").Foreground, Application.Current.FindResource("SecondaryText"))
                    && combo.SelectedItem as string == "Second", "Re-enabling native controls lost caption colors or the selected value.");
                dashboard.Navigate("general"); await Idle();
                var sidebar = Descendants<ListBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Settings sections");
                Require(sidebar.Items.OfType<ListBoxItem>().All(x => Descendants<System.Windows.Shapes.Path>(x).Any()
                    && !Descendants<Border>(x.Content as DependencyObject ?? x).Any(b => b.Width == 20 && b.Height == 20)),
                    "The sidebar retained colored Mac-style icon tiles.");
                Require(Descendants<ComboBox>(dashboard).All(x => x.ActualHeight >= 32), "A settings picker clips the native control height.");
                Capture(dashboard, Path.Combine(directory, "windows-platform-settings-" + (dark ? "dark" : "light") + ".png"));
            }
            SettingsTheme.Apply(wasDark, highContrast: true); await Idle();
            Require(ReferenceEquals(Application.Current.FindResource("PrimaryText"), SystemColors.WindowTextBrush)
                && ReferenceEquals(Application.Current.FindResource("AccentBrush"), SystemColors.HighlightBrush), "High-contrast aliases lost Windows system colors.");
            checks.Add("Light/dark controls use Windows accent/text resources and preserve edits; sidebar uses monochrome glyphs and unclipped pickers");
            checks.Add("Disabled selector labels, captioned checkbox text and action glyphs inherit native state colors; re-enabling preserves caption colors and selection");
            checks.Add("Custom surfaces use system colors in synthetic high-contrast mode; real OS high-contrast interaction remains a separate check");
        }
        finally { fixture?.Close(); SettingsTheme.Apply(wasDark, wasContrast); }
        File.WriteAllText(Path.Combine(directory, "windows-platform-controls.json"), JsonSerializer.Serialize(new { completed = true, checks }, JsonOptions));
    }
}
