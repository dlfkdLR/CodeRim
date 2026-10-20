using CodeRim.Core.Domain;

namespace CodeRim.Core.Tests;

public sealed class NotchControlLayoutTests
{
    // Golden dimensions from the real macOS NotchLayout/NotchDesign probe,
    // using its measured17-point ring label rather than a Windows-only18-point row.
    [Theory]
    [InlineData(NotchEdge.Right, 1, 1d, 69.94871794871794, 193.56410256410254)]
    [InlineData(NotchEdge.Left, 3, 0.8, 55.95897435897436, 318.88)]
    [InlineData(NotchEdge.Right, 70, 1.25, 87.43589743589743, 9084.128205128205)]
    public void BodyMatchesMacOSMeasuredLabelHeight(NotchEdge edge, int count, double scale, double depth, double length)
    {
        var fit = NotchMetrics.Fit(edge, count, scale, 100_000);
        Assert.Equal(depth, fit.Depth, 8);
        Assert.Equal(length, fit.Length, 8);
    }

    [Theory]
    [InlineData(NotchEdge.Right, false, 0.75)]
    [InlineData(NotchEdge.Right, true, 0.0)]
    [InlineData(NotchEdge.Left, false, 0.5)]
    [InlineData(NotchEdge.Left, true, 0.25)]
    [InlineData(NotchEdge.Top, false, 0.5)]
    [InlineData(NotchEdge.Top, true, 0.5)]
    [InlineData(NotchEdge.Bottom, false, 0.25)]
    [InlineData(NotchEdge.Bottom, true, 0.25)]
    public void RestingArcFacesTheMacFlare(NotchEdge edge, bool above, double fraction) =>
        Assert.Equal(fraction, NotchMetrics.RestingArcStart(edge, above));

    [Theory]
    [InlineData(NotchEdge.Top)]
    [InlineData(NotchEdge.Bottom)]
    public void HorizontalRingHasEqualEndPaddingAndTrailingControls(NotchEdge edge)
    {
        Assert.Equal(NotchMetrics.StartPadding(edge), NotchMetrics.EndPadding(edge));
        Assert.False(NotchMetrics.ControlsAtStart(edge, "Start", 200, 1, 1200, 450));
    }
    [Theory]
    [InlineData(NotchEdge.Left)]
    [InlineData(NotchEdge.Right)]
    public void AutomaticControlsMoveOnlyWhenTheirFullHitRegionWouldRunOffScreen(NotchEdge edge)
    {
        // A 200-DIP body centered on a 1000-DIP screen leaves400 below it.
        // At offset300 there is still100; at offset340 there is only60.
        Assert.False(NotchMetrics.ControlsAtStart(edge, "Auto", 200, 1, 1000, 300));
        Assert.True(NotchMetrics.ControlsAtStart(edge, "Auto", 200, 1, 1000, 340));
        Assert.False(NotchMetrics.ControlsAtStart(edge, "Auto", 200, 1, 1000, -400));
        Assert.True(NotchMetrics.ControlsAtStart(edge, "Start", 200, 1, 1000, 0));
        Assert.False(NotchMetrics.ControlsAtStart(edge, "End", 200, 1, 1000, 400));
        Assert.Equal(66, NotchMetrics.ControlExtent);
    }
}
