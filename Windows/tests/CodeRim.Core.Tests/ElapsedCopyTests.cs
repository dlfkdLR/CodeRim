using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class ElapsedCopyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    [Theory]
    [InlineData(-120, "just now")]
    [InlineData(0, "just now")]
    [InlineData(44, "just now")]
    [InlineData(45, "1 min ago")]
    [InlineData(89, "1 min ago")]
    [InlineData(90, "2 min ago")]
    [InlineData(3569, "59 min ago")]
    [InlineData(3570, "1 hr ago")]
    [InlineData(3600, "1 hr ago")]
    [InlineData(3660, "1 hr 1 min ago")]
    [InlineData(86400, "1 d 0 hr 0 min ago")]
    [InlineData(93780, "1 d 2 hr 3 min ago")]
    public void ProviderAgeMatchesReferenceBoundaries(int seconds, string expected)
        => Assert.Equal(expected, ElapsedCopy.Ago(Now.AddSeconds(-seconds), Now));
    [Fact]
    public void OffsetDoesNotChangeElapsedTime()
        => Assert.Equal("1 hr ago", ElapsedCopy.Ago(Now.AddHours(-1).ToOffset(TimeSpan.FromHours(9)), Now));
}
