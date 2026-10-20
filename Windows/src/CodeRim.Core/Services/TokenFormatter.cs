using System.Globalization;

namespace CodeRim.Core.Services;

public enum TokenNumberStyle
{
    Compact,
    Detailed
}

public static class TokenFormatter
{
    public static string Format(long value, TokenNumberStyle style) =>
        style == TokenNumberStyle.Detailed
            ? value.ToString("N0", CultureInfo.CurrentCulture)
            : Compact(value);

    private static string Compact(long value)
    {
        var (divisor, suffix) = value switch
        {
            >= 999_500_000 => (1_000_000_000d, "B"),
            >= 999_500 => (1_000_000d, "M"),
            >= 1_000 => (1_000d, "K"),
            _ => (0d, string.Empty)
        };

        if (divisor == 0)
        {
            return value.ToString(CultureInfo.CurrentCulture);
        }

        var scaled = value / divisor;
        var decimals = scaled >= 100 ? 0 : scaled >= 10 ? 1 : 2;
        var format = decimals == 0 ? "0" : $"0.{new string('#', decimals)}";
        return scaled.ToString(format, CultureInfo.CurrentCulture) + suffix;
    }
}
