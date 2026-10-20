using System.Globalization;
using System.Text;

namespace CodeRim.Core.Domain;

/// <summary>Local calendar labels shared by resets, server history and charts.</summary>
public static class CalendarDateText
{
    public static string? MonthDay(DateTime date, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        try { return date.ToString(MonthDayFormat(culture), culture); }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (FormatException) { return null; }
    }

    // Foundation's MMM d template preserves locale-specific order and literals.
    // Use the matching local month/day pattern, abbreviating only format tokens.
    private static string MonthDayFormat(CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.MonthDayPattern;
        var result = new StringBuilder(pattern.Length);
        for (var index = 0; index < pattern.Length;)
        {
            var token = pattern[index++];
            result.Append(token);
            if (token is '\\' or '%' && index < pattern.Length) { result.Append(pattern[index++]); continue; }
            if (token is '\'' or '"')
            {
                while (index < pattern.Length)
                {
                    var quoted = pattern[index++]; result.Append(quoted);
                    if (quoted == '\\' && index < pattern.Length) result.Append(pattern[index++]);
                    else if (quoted == token) break;
                }
                continue;
            }
            var count = 1;
            while (index < pattern.Length && pattern[index] == token) { index++; count++; }
            var length = token == 'M' && count == 4 ? 3 : token == 'd' && count == 2 ? 1 : count;
            result.Append(token, length - 1);
        }
        // A single custom day/month token would otherwise become a standard
        // date format when passed to ToString (for example dd -> d).
        return result.Length == 1 && result[0] is 'd' or 'M' ? "%" + result : result.ToString();
    }
}
