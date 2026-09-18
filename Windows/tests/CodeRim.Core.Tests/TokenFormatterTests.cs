using System.Globalization;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class TokenFormatterTests
{
    [Fact]
    public void FormatsCompactBoundaries()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("999", TokenFormatter.Format(999, TokenNumberStyle.Compact));
            Assert.Equal("1K", TokenFormatter.Format(1_000, TokenNumberStyle.Compact));
            Assert.Equal("1M", TokenFormatter.Format(999_500, TokenNumberStyle.Compact));
            Assert.Equal("177M", TokenFormatter.Format(177_000_000, TokenNumberStyle.Compact));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
