using CodeRim.Core.Services;
using Xunit;

namespace CodeRim.Core.Tests;

public sealed class GitHubDevicePolicyTests
{
    [Theory]
    [InlineData(null, null, 5, 900)]
    [InlineData(int.MinValue, int.MinValue, 1, 60)]
    [InlineData(17, 541, 17, 541)]
    [InlineData(int.MaxValue, int.MaxValue, 60, 1800)]
    public void ScheduleRequiresAllThreeCodeFieldsAndBoundsBothTimers(
        int? interval, int? valid, int expectedInterval, int expectedValid)
    {
        for (var mask = 0; mask < 8; mask++)
        {
            var result = GitHubDevicePolicy.Prepare((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, interval, valid);
            Assert.Equal(mask == 7 ? 1 : 0, result.Prompt);
            Assert.Equal(expectedInterval, result.IntervalSeconds);
            Assert.Equal(expectedValid, result.ValidSeconds);
        }
    }

    [Fact]
    public void RepeatedSlowDownAccumulatesAndApprovalWinsOverError()
    {
        var interval = TimeSpan.FromSeconds(60);
        for (var i = 1; i <= 3; i++)
        {
            var slow = GitHubDevicePolicy.Next(false, "slow_down", interval);
            Assert.Equal(DevicePollAction.Wait, slow.Action);
            interval = TimeSpan.FromTicks(slow.IntervalTicks);
            Assert.Equal(TimeSpan.FromSeconds(60 + 5 * i), interval);
        }
        Assert.Equal(interval.Ticks, GitHubDevicePolicy.Next(false, "authorization_pending", interval).IntervalTicks);
        Assert.Equal(DevicePollAction.Store, GitHubDevicePolicy.Next(true, "access_denied", interval).Action);
        Assert.Equal(DevicePollAction.Refused, GitHubDevicePolicy.Next(false, "SLOW_DOWN", interval).Action);
        Assert.Throws<OverflowException>(() => GitHubDevicePolicy.Next(false, "slow_down", TimeSpan.MaxValue));
    }
}
