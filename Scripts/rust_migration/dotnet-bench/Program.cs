using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeRim.Core.Domain;

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
