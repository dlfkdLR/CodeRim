using CodeRim.Core.Domain;

namespace CodeRim.Core.Tests;

public sealed class NotchDisplayFitTests
{
    [Theory]
    [InlineData(NotchEdge.Left)]
    [InlineData(NotchEdge.Right)]
    [InlineData(NotchEdge.Top)]
    [InlineData(NotchEdge.Bottom)]
    public void SmallWorkAreaFitsBodyControlsAndDepthWithoutChangingRequestedScale(NotchEdge edge)
    {
        const double requested = 1.25;
        foreach (var along in new[] { 1d, 50, 150, 250 })
        foreach (var depth in new[] { 1d, 40, 200 })
        foreach (var count in new[] { 0, 1, 70 })
        {
            var scale = NotchMetrics.FitScale(edge, requested, along, depth, true);
            var fit = NotchMetrics.Fit(edge, count, scale, along);
            var outerDepth = Math.Max(fit.Depth, (NotchMetrics.Curl + NotchMetrics.OrbArcRadius + NotchMetrics.OrbStroke / 2) * scale);
            Assert.InRange(scale, double.Epsilon, requested);
            Assert.InRange(fit.Length + NotchMetrics.ControlExtent * scale, 0, along + 1e-8);
            Assert.InRange(outerDepth, 0, depth + 1e-8);
            Assert.True(fit.Capacity >= 1);
        }
    }

    [Theory]
    [InlineData(NotchEdge.Left)]
    [InlineData(NotchEdge.Right)]
    [InlineData(NotchEdge.Top)]
    [InlineData(NotchEdge.Bottom)]
    public void NormalWorkAreaPreservesExistingGoldenSizeAndFoldedPillFits(NotchEdge edge)
    {
        foreach (var requested in new[] { 0.8, 1d, 1.25 })
        {
            Assert.Equal(requested, NotchMetrics.FitScale(edge, requested, 1000, 1000, true));
            Assert.Equal(requested, NotchMetrics.FitScale(edge, requested, 1000, 1000, false));
            var folded = NotchMetrics.FitScale(edge, requested, 40, 4, false);
            Assert.InRange(NotchMetrics.PillLength * folded, 0, 40 + 1e-8);
            Assert.InRange(NotchMetrics.PillDepth * folded, 0, 4 + 1e-8);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidRequestedScaleUsesTheExistingSafeDefault(double requested) =>
        Assert.Equal(1, NotchMetrics.FitScale(NotchEdge.Right, requested, 1000, 1000, true));
}
