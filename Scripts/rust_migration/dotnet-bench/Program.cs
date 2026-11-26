using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

if (args is ["parse-tokens" or "parse-mixed"])
{
    TokenParserBenchmark.Run(args[0]);
    return;
}

if (args is ["aggregate"])
{
    const int length = 8192;
    const int repeats = 64;
    var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    var events = Enumerable.Range(0, length).Select(i =>
        new UsageEvent(i.ToString(System.Globalization.CultureInfo.InvariantCulture), now.AddHours(-(i % 1024)), new TokenUsage(29, 7, 5, 2))
        { Repaired = i == 1024 }).ToArray();
    long Fold() {
        var result = UsageAggregation.Aggregate(events, now, WeekStart.Monday, false, TimeZoneInfo.Utc);
        if (result.AllTime.InputTokens != length * 29L || result.Quality != DataQuality.Partial)
            throw new InvalidOperationException("Period accounting mismatch.");
        return result.Today.TotalTokens + result.Week.TotalTokens + result.Month.TotalTokens + result.AllTime.TotalTokens;
    }
    for (var i = 0; i < 8; i++) Fold();
    var before = GC.GetAllocatedBytesForCurrentThread();
    var timer = Stopwatch.GetTimestamp();
    long sum = 0;
    for (var i = 0; i < repeats; i++) sum += Fold();
    var duration = Stopwatch.GetElapsedTime(timer);
    var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
    Console.WriteLine(JsonSerializer.Serialize(new {
        elapsedMs = duration.TotalMilliseconds, allocatedBytes = bytes, checksum = sum,
        observations = length * repeats, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        runtime = RuntimeInformation.FrameworkDescription
    }));
    return;
}

// The measured region invokes the public host entry point with pre-parsed observations.
const int observationsPerSession = 4096;
const int rounds = 128;
var observations = Enumerable.Range(1, observationsPerSession).Select(i =>
{
    var cumulative = new TokenUsage(i * 29L, i * 7L, i * 5L, i * 2L);
    return new TokenObservation(DateTimeOffset.UnixEpoch.AddTicks(i), null, i == 1 ? cumulative : null, cumulative);
}).ToArray();

Run(observations, 32);
var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
var start = Stopwatch.GetTimestamp();
var checksum = Run(observations, rounds);
var elapsed = Stopwatch.GetElapsedTime(start);
var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
if (checksum != (long)observationsPerSession * rounds * 34)
    throw new InvalidOperationException("Accounting checksum mismatch.");
Console.WriteLine(JsonSerializer.Serialize(new
{
    elapsedMs = elapsed.TotalMilliseconds,
    allocatedBytes = allocated,
    checksum,
    observations = observationsPerSession * rounds,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    runtime = RuntimeInformation.FrameworkDescription
}));

static long Run(TokenObservation[] observations, int count)
{
    long total = 0;
    for (var i = 0; i < count; i++)
    {
        var state = UsageNormalizationState.Empty;
        foreach (var observation in observations)
        {
            var result = UsageNormalizer.Normalize(observation, state);
            total += result.Delta.GetValueOrDefault().TotalTokens;
            state = result.State;
        }
    }
    return total;
}
