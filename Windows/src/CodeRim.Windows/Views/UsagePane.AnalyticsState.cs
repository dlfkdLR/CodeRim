using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodeRim.Core.Domain;

namespace CodeRim.Windows.Views;

internal sealed partial class UsagePane
{
    private DataQuality AnalyticsQuality(TokenUsage rangeUsage)
    {
        if (rangeUsage.IsZero) return DataQuality.Unavailable;
        var snapshot = store.Usage.GetValueOrDefault(provider);
        return snapshot?.RetainsPartialHistory == true ? DataQuality.Partial : snapshot?.Quality ?? DataQuality.Unavailable;
    }

    private bool AnalyticsReady()
    {
        var snapshot = store.Usage.GetValueOrDefault(provider);
        if (snapshot is null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new LimitActivityIndicator { Margin = new Thickness(0, 0, 10, 0) });
            var text = Ui.Text("Analytics are ready to load", 13, "#A6A6AA"); text.Margin = new Thickness(0); row.Children.Add(text);
            var pending = new Grid { MinHeight = 180 }; pending.Children.Add(row);
            AutomationProperties.SetAutomationId(pending, "usage.analytics.loading"); readings.Children.Add(pending); return false;
        }
        if (snapshot.Quality == DataQuality.Error && snapshot.UpdatedAt is null)
        {
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M1,1 V15 H15 M3,11 L6,7 L10,9 L14,3"),
                Width = 32, Height = 32, Stretch = Stretch.Uniform, StrokeThickness = 1.5, Margin = new Thickness(0, 0, 0, 12) };
            icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText"); content.Children.Add(icon);
            var title = Ui.Text(destination switch { "projects" => "Projects Unavailable", "sessions" => "Sessions Unavailable", _ => "Usage Unavailable" },
                17, weight: FontWeights.SemiBold); title.TextAlignment = TextAlignment.Center; content.Children.Add(title);
            var detail = Ui.Text("Analytics are unavailable", 13, "#A6A6AA"); detail.TextAlignment = TextAlignment.Center; content.Children.Add(detail);
            var error = new Grid { MinHeight = 180 }; error.Children.Add(content);
            AutomationProperties.SetAutomationId(error, "usage.analytics.unavailable"); readings.Children.Add(error); return false;
        }
        if (snapshot.Quality is DataQuality.Stale or DataQuality.Error)
        {
            var row = new DockPanel { Margin = IsAnalyticsList ? new Thickness(16, 8, 16, 8) : new Thickness(0, 0, 0, 16) };
            var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M6,1 L11,11 H1 Z M6,4 V7 M6,9 V9.2"),
                Width = 12, Height = 12, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round, Margin = new Thickness(0, 0, 6, 0) };
            icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText"); DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
            var text = Ui.Text("Showing the last analytics snapshot", 11, "#A6A6AA"); text.Margin = new Thickness(0); row.Children.Add(text);
            AutomationProperties.SetAutomationId(row, "usage.analytics.stale"); readings.Children.Add(row);
        }
        return true;
    }
}
