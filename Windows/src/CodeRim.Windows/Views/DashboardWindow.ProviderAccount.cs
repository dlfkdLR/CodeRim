using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Domain;

namespace CodeRim.Windows.Views;

internal sealed partial class DashboardWindow
{
    private StackPanel? providerAccountSection;
    private FrameworkElement? providerAccountLabelRow;
    private FrameworkElement? providerAccountPlanRow;
    private TextBlock? providerAccountLabel;
    private TextBlock? providerAccountPlan;
    private TextBlock? providerAccountSource;
    private System.Windows.Documents.Hyperlink? providerAccountManage;
    private Uri? providerAccountDestination;
    private string? providerAccountId;

    private void ResetProviderAccount()
    {
        providerAccountSection = null; providerAccountLabelRow = null; providerAccountPlanRow = null;
        providerAccountLabel = null; providerAccountPlan = null; providerAccountSource = null;
        providerAccountManage = null; providerAccountDestination = null; providerAccountId = null;
    }
    private void AddProviderAccount(string id)
    {
        providerAccountId = id;
        providerAccountLabel = Value(); providerAccountPlan = Value(); providerAccountSource = Value();
        static TextBlock Value()
        {
            var text = Ui.Text("", 13, "#A6A6AA"); text.MaxWidth = 240;
            text.TextTrimming = TextTrimming.CharacterEllipsis; text.TextWrapping = TextWrapping.NoWrap; return text;
        }
        AutomationProperties.SetAutomationId(providerAccountLabel, "provider.identity.label");
        AutomationProperties.SetAutomationId(providerAccountPlan, "provider.identity.plan");
        AutomationProperties.SetAutomationId(providerAccountSource, "provider.identity.source");
        providerAccountLabelRow = SettingsUi.Row("Signed in as", providerAccountLabel);
        providerAccountPlanRow = SettingsUi.Row("Plan", providerAccountPlan);
        var rows = new List<UIElement> { providerAccountLabelRow, providerAccountPlanRow,
            SettingsUi.Row("Credential from", providerAccountSource) };
        if (ProviderAccountLinks.UsagePage(id) is { } destination)
        {
            var row = (DockPanel)SettingsUi.Link("Open usage page", destination,
                "M8,1 A7,7 0 1 1 7.99,1 M5,11 L7,7 L11,5 L9,9 Z",
                _ => { if (providerAccountDestination is { } current) OpenUrl(current.AbsoluteUri); });
            providerAccountManage = row.Children.OfType<TextBlock>().SelectMany(text => text.Inlines.OfType<System.Windows.Documents.Hyperlink>()).Single();
            rows.Add(row);
        }
        providerAccountSection = SettingsUi.Section("Account", rows.ToArray());
        AutomationProperties.SetAutomationId(providerAccountSection, "provider.identity");
        body.Children.Add(providerAccountSection);
    }
    private void UpdateProviderAccount()
    {
        if (providerAccountSection is null) return;
        var display = store.ProviderAccountDisplay(providerAccountId!);
        var account = display.Account;
        providerAccountDestination = account is null ? null : ProviderAccountLinks.UsagePage(providerAccountId!, account.Region);
        if (providerAccountManage is not null) providerAccountManage.NavigateUri = providerAccountDestination;
        providerAccountSection.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;
        providerAccountLabel!.Text = account?.Label ?? "";
        providerAccountPlan!.Text = account is not null && display.Plan is { } plan
            ? CultureInfo.CurrentCulture.TextInfo.ToTitleCase(plan.ToLower(CultureInfo.CurrentCulture)) : "";
        providerAccountSource!.Text = account?.Source ?? "";
        providerAccountLabel.ToolTip = providerAccountLabel.Text; providerAccountPlan.ToolTip = providerAccountPlan.Text;
        providerAccountSource.ToolTip = providerAccountSource.Text;
        providerAccountLabelRow!.Visibility = account?.Label is null ? Visibility.Collapsed : Visibility.Visible;
        providerAccountPlanRow!.Visibility = account is null || display.Plan is null ? Visibility.Collapsed : Visibility.Visible;
        // Optional account/plan rows must not leave a divider above the first
        // visible row. Do not rebuild the link or any connection input on polls.
        var content = (StackPanel)((Border)providerAccountSection.Children[1]).Child;
        var previousVisible = false;
        for (var index = 0; index < content.Children.Count; index += 2)
        {
            var visible = content.Children[index].Visibility == Visibility.Visible;
            if (index > 0) content.Children[index - 1].Visibility = previousVisible && visible ? Visibility.Visible : Visibility.Collapsed;
            previousVisible |= visible;
        }
    }
}
