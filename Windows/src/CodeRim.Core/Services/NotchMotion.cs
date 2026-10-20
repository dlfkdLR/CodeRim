namespace CodeRim.Core.Services;

/// <summary>Timing shared with macOS NotchMotion, expressed in seconds.</summary>
public static class NotchMotion
{
    public const double Unfold = 0.42, Contents = 0.36, Glide = 0.5, Crossfade = 0.16;
    public const double Reading = 0.9, ReadingReset = 0.85, Refresh = 0.95;
    public const double ActivityPeriod = 1.1, WaitingPeriod = 1.8, GradientPeriod = 3;
    public static double Stagger(int index) => Math.Min(Math.Max(0, index) * 0.045, 0.18);
    public static double EaseInOut(double progress) => (1 - Math.Cos(Math.PI * Math.Clamp(progress, 0, 1))) / 2;
    public static double Spring(double progress, double damping)
    {
        if (progress <= 0) return 0;
        if (progress >= 1) return 1;
        damping = Math.Clamp(damping, 0.01, 0.999);
        var frequency = Math.Tau * 2;
        var damped = Math.Sqrt(1 - damping * damping);
        return 1 - Math.Exp(-damping * frequency * progress) *
            (Math.Cos(frequency * damped * progress) + damping / damped * Math.Sin(frequency * damped * progress));
    }
    public static double Phase(double seconds, double period) => ((seconds % period) + period) % period / period;
}
