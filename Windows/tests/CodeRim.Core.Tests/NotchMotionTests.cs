using CodeRim.Core.Services;
using Xunit;

namespace CodeRim.Core.Tests;

public sealed class NotchMotionTests
{
    [Theory]
    [InlineData(0.62)] [InlineData(0.78)] [InlineData(0.82)] [InlineData(0.86)] [InlineData(0.9)]
    public void SpringStartsAndSettlesWithoutInvalidGeometry(double damping)
    {
        Assert.Equal(0, NotchMotion.Spring(0, damping)); Assert.Equal(1, NotchMotion.Spring(1, damping));
        var samples = Enumerable.Range(0, 121).Select(x => NotchMotion.Spring(x / 120d, damping)).ToArray();
        Assert.All(samples, x => Assert.InRange(x, 0, 1.1));
        Assert.InRange(samples[30], 0.7, 1.1);
        Assert.InRange(samples[^2], 0.999, 1.001);
    }
    [Fact]
    public void ResetRetractsContinuouslyToEmptyWithoutReverseOrEndpointDot()
    {
        var last = 0.91;
        for (var frame = 0; frame <= 120; frame++)
        {
            var next = 0.91 * (1 - NotchMotion.EaseInOut(frame / 120d));
            Assert.InRange(next, 0, last); last = next;
        }
        Assert.Equal(0, last);
    }
    [Fact]
    public void LongProviderListsHaveBoundedStagger()
    {
        Assert.Equal(0, NotchMotion.Stagger(-1)); Assert.Equal(0.045, NotchMotion.Stagger(1));
        Assert.Equal(0.18, NotchMotion.Stagger(70));
    }
    [Fact]
    public void LoopPeriodsWrapWithoutDriftAndRemainIndependent()
    {
        foreach (var period in new[] { NotchMotion.ActivityPeriod, NotchMotion.WaitingPeriod, NotchMotion.GradientPeriod })
        {
            Assert.Equal(0.5, NotchMotion.Phase(period / 2, period), 10);
            Assert.InRange(NotchMotion.Phase(-0.1, period), 0, 1);
            Assert.Equal(0, NotchMotion.Phase(period, period), 10);
        }
        Assert.NotEqual(NotchMotion.ActivityPeriod, NotchMotion.GradientPeriod);
    }
}
