namespace CodeRim.Core.Domain;

public readonly record struct TokenUsage(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long? CacheWriteInputTokens = null)
{
    public static TokenUsage Zero { get; } = new(0, 0, 0, 0);

    public bool IsZero => InputTokens == 0 && CachedInputTokens == 0 && OutputTokens == 0 && (CacheWriteInputTokens is null or 0);

    public long TotalTokens => SaturatingAdd(InputTokens, OutputTokens);

    public bool IsValid => InputTokens >= 0
        && CachedInputTokens >= 0
        && OutputTokens >= 0
        && (CacheWriteInputTokens is null || CacheWriteInputTokens >= 0)
        && CachedInputTokens <= InputTokens
        && (CacheWriteInputTokens is null || CacheWriteInputTokens <= InputTokens - CachedInputTokens);

    public long UncachedInputTokens => Math.Max(0, InputTokens - CachedInputTokens - (CacheWriteInputTokens ?? 0));

    public TokenUsage Add(TokenUsage other) => new(
        SaturatingAdd(InputTokens, other.InputTokens),
        SaturatingAdd(CachedInputTokens, other.CachedInputTokens),
        SaturatingAdd(OutputTokens, other.OutputTokens),
        OptionalAdd(CacheWriteInputTokens, other.CacheWriteInputTokens));

    public TokenUsage SubtractFloorAtZero(TokenUsage previous) => new(
        Math.Max(0, InputTokens - previous.InputTokens),
        Math.Max(0, CachedInputTokens - previous.CachedInputTokens),
        Math.Max(0, OutputTokens - previous.OutputTokens),
        CacheWriteInputTokens is { } written && previous.CacheWriteInputTokens is { } prior ? Math.Max(0, written - prior) : null);

    public TokenUsage ComponentWiseMaximum(TokenUsage other) => new(
        Math.Max(InputTokens, other.InputTokens),
        Math.Max(CachedInputTokens, other.CachedInputTokens),
        Math.Max(OutputTokens, other.OutputTokens),
        CacheWriteInputTokens is { } written && other.CacheWriteInputTokens is { } prior ? Math.Max(written, prior) : CacheWriteInputTokens ?? other.CacheWriteInputTokens);

    public bool IsComponentWiseAtLeast(TokenUsage other) =>
        InputTokens >= other.InputTokens
        && CachedInputTokens >= other.CachedInputTokens
        && OutputTokens >= other.OutputTokens
        && (other.CacheWriteInputTokens is null || CacheWriteInputTokens is { } written && written >= other.CacheWriteInputTokens);

    public bool HasCounterDecrease(TokenUsage previous) =>
        InputTokens < previous.InputTokens || CachedInputTokens < previous.CachedInputTokens
        || OutputTokens < previous.OutputTokens
        || (CacheWriteInputTokens is { } current && previous.CacheWriteInputTokens is { } prior && current < prior);

    private static long? OptionalAdd(long? left, long? right) => left is { } l && right is { } r ? SaturatingAdd(l, r) : null;

    private static long SaturatingAdd(long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }
}
