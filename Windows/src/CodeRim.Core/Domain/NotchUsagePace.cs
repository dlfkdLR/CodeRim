using System.Globalization;

namespace CodeRim.Core.Domain;

/// <summary>The notch's reserved/deficit metric, distinct from Account Limits' projection.</summary>
public sealed record NotchUsagePace(double PercentagePoints)
{
    public bool IsDeficit => PercentagePoints > 0;
    public string Summary
    {
        get
        {
            var magnitude = Math.Abs(PercentagePoints);
            var rounded = Math.Round(magnitude, 1, MidpointRounding.AwayFromZero);
            var value = rounded == 0 && magnitude > 0 ? "<0.1" : rounded.ToString("0.#", CultureInfo.InvariantCulture);
            return value + "% " + (IsDeficit ? "deficit" : "reserved");
        }
    }

    public static NotchUsagePace? For(LimitWindow window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.UsedPercent is not { } used || !double.IsFinite(used) || used < 0
            || window.DurationMinutes <= 0 || window.ResetsAt is not { } reset) return null;
        var remaining = (reset - now).TotalSeconds;
        if (remaining <= 0) return null;
        var elapsed = 1 - Math.Min(remaining / (window.DurationMinutes * 60d), 1);
        return new((Math.Min(used / 100, 1) - elapsed) * 100);
    }
}
