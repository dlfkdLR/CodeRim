using System.Globalization;

namespace CodeRim.Core.Services;

/// <summary>Relative time copy shared by provider state and task duration, matching the Mac reference.</summary>
public static class ElapsedCopy
{
    public static string Ago(DateTimeOffset since, DateTimeOffset now)
    {
        var text = Text(since, now);
        return text == "just now" ? text : text + " ago";
    }
    public static string Text(DateTimeOffset since, DateTimeOffset now)
    {
        var seconds = Math.Max(0, (now - since).TotalSeconds);
        if (seconds < 45) return "just now";
        var minutes = (long)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        if (minutes < 60) return Math.Max(1, minutes).ToString(CultureInfo.InvariantCulture) + " min";
        var hours = minutes / 60; var rest = minutes % 60;
        if (hours >= 24) return FormattableString.Invariant($"{hours / 24} d {hours % 24} hr {rest} min");
        return hours.ToString(CultureInfo.InvariantCulture) + " hr" + (rest == 0 ? "" : " " + rest.ToString(CultureInfo.InvariantCulture) + " min");
    }
}
