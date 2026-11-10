using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using CodeRim.Core.Domain;
using CodeRim.Core.Parsing;

NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) =>
    name == "normalization_trial" ? NativeLibrary.Load(args[1]) : IntPtr.Zero);
Check(Marshal.SizeOf<TrialUsage>() == 32 && Marshal.SizeOf<TrialState>() == 64
    && Marshal.SizeOf<TrialObservation>() == 88 && Marshal.SizeOf<TrialResult>() == 120, "ABI layout mismatch");
var fixtures = JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(args[0]), JsonSerializerOptions.Web)!;
Check(fixtures.Length > 0, "empty fixture");
string?[] diagnostics = [null, "out-of-order token snapshot ignored", "missing cumulative token usage",
    "initial cumulative baseline is unresolved", "cumulative counter restarted",
    "cumulative token usage decreased or interleaved", "inconsistent token delta clamped",
    "inconsistent token usage clamped"];
DataQuality[] qualities = [DataQuality.Exact, DataQuality.Partial, DataQuality.Stale, DataQuality.Unavailable, DataQuality.Error];
var workload = new List<(UsageNormalizationState State, TokenObservation Observation, TrialState RustState, TrialObservation RustObservation)>();
foreach (var fixture in fixtures)
{
    var native = UsageNormalizationState.Empty;
    var rust = new TrialState();
    foreach (var step in fixture.Steps)
    {
        var observation = new TokenObservation(DateTimeOffset.UnixEpoch.AddSeconds(step.OccurredAt), null, step.LastUsage, step.CumulativeUsage);
        var ffi = new TrialObservation(Packed(step.CumulativeUsage), Packed(step.LastUsage), step.OccurredAt,
            step.CumulativeUsage is null ? 0 : 1, step.LastUsage is null ? 0 : 1);
        workload.Add((native, observation, rust, ffi));
        var expected = UsageNormalizer.Normalize(observation, native);
        var actual = Native.Normalize(rust, ffi, 1);
        Check(expected.Delta == (actual.HasDelta == 0 ? null : Unpacked(actual.Delta)), fixture.Name);
        Check(expected.State.CumulativeHighWaterMark == (actual.State.HasUsage == 0 ? null : Unpacked(actual.State.Usage)), fixture.Name);
        Check(expected.State.LastObservedAt == (actual.State.HasTime == 0 ? null : DateTimeOffset.UnixEpoch.AddSeconds(actual.State.ObservedAt)), fixture.Name);
        Check(expected.State.Quality == qualities[actual.State.Quality], fixture.Name);
        Check(expected.Diagnostic == diagnostics[actual.Diagnostic] && expected.Repaired == (actual.Repaired != 0), fixture.Name);
        native = expected.State;
        rust = actual.State;
    }
}
const int iterations = 1_000_000;
var mode = args.Length > 2 ? args[2] : "both";
Check(mode is "native" or "rust" or "both", "invalid mode");
List<double> nativeTimes = [], rustTimes = [];
List<long> nativeBytes = [], rustBytes = [];
long nativeChecksum = 0, rustChecksum = 0;
for (var pass = 0; pass < 4; pass++)
{
    if (mode != "rust")
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        long checksum = 0;
        for (var index = 0; index < iterations; index++)
        {
            var row = workload[index % workload.Count];
            checksum = unchecked(checksum + (UsageNormalizer.Normalize(row.Observation, row.State).Delta?.InputTokens ?? 0));
        }
        timer.Stop();
        nativeBytes.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
        nativeTimes.Add(timer.Elapsed.TotalNanoseconds);
        nativeChecksum = checksum;
    }
    if (mode != "native")
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        long checksum = 0;
        for (var index = 0; index < iterations; index++)
        {
            var row = workload[index % workload.Count];
            var result = Native.Normalize(row.RustState, row.RustObservation, 1);
            checksum = unchecked(checksum + (result.HasDelta == 0 ? 0 : result.Delta.Input));
        }
        timer.Stop();
        rustBytes.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
        rustTimes.Add(timer.Elapsed.TotalNanoseconds);
        rustChecksum = checksum;
    }
}
if (mode == "both") Check(nativeChecksum == rustChecksum, "benchmark checksum mismatch");
var lines = fixtures.SelectMany(fixture => fixture.Steps.Select(step =>
    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        type = "event_msg", timestamp = "2026-08-27T01:02:03.125Z",
        payload = new { type = "token_count", info = new
        {
            last_token_usage = Raw(step.LastUsage), total_token_usage = Raw(step.CumulativeUsage)
        } }
    })))).ToArray();
const int pipelineIterations = 20_000;
List<double> pipelineNative = [], pipelineRust = [];
HashSet<long> pipelineChecksums = [];
for (var pass = 0; pass < 4; pass++)
{
    foreach (var variant in new[] { "native", "rust" }.Where(variant => mode == "both" || mode == variant))
    {
        var timer = Stopwatch.StartNew();
        long checksum = 0;
        for (var index = 0; index < pipelineIterations; index++)
        {
            if (CodexJsonlParser.Parse(lines[index % lines.Length]).Token is not { } observation) continue;
            checksum = unchecked(checksum + (observation.CumulativeUsage?.InputTokens ?? observation.LastUsage?.InputTokens ?? 0));
            if (variant == "native")
                checksum = unchecked(checksum + (UsageNormalizer.Normalize(observation, UsageNormalizationState.Empty).Delta?.InputTokens ?? 0));
            else
            {
                var ffi = new TrialObservation(Packed(observation.CumulativeUsage), Packed(observation.LastUsage),
                    (observation.OccurredAt - DateTimeOffset.UnixEpoch).TotalSeconds,
                    observation.CumulativeUsage is null ? 0 : 1, observation.LastUsage is null ? 0 : 1);
                var result = Native.Normalize(new TrialState(), ffi, 1);
                checksum = unchecked(checksum + (result.HasDelta == 0 ? 0 : result.Delta.Input));
            }
        }
        timer.Stop();
        (variant == "native" ? pipelineNative : pipelineRust).Add(timer.Elapsed.TotalNanoseconds);
        pipelineChecksums.Add(checksum);
    }
}
Check(pipelineChecksums.Count == 1, "pipeline checksum mismatch");
Console.WriteLine(JsonSerializer.Serialize(new { language = "csharp", cases = fixtures.Length, transitions = workload.Count,
    iterations, mode, nativeNanoseconds = nativeTimes, rustNanoseconds = rustTimes, nativeAllocatedBytes = nativeBytes,
    rustAllocatedBytes = rustBytes, checksum = mode == "rust" ? rustChecksum : nativeChecksum,
    pipelineIterations, pipelineNativeNanoseconds = pipelineNative, pipelineRustNanoseconds = pipelineRust,
    pipelineChecksum = pipelineChecksums.Single() }));

static void Check(bool condition, string context) { if (!condition) throw new InvalidOperationException(context); }
static TrialUsage Packed(TokenUsage? usage) => new(usage?.InputTokens ?? 0, usage?.CachedInputTokens ?? 0,
    usage?.OutputTokens ?? 0, usage?.CacheWriteInputTokens ?? -1);
static TokenUsage Unpacked(TrialUsage usage) => new(usage.Input, usage.Cached, usage.Output, usage.Written == -1 ? null : usage.Written);
static Dictionary<string, long>? Raw(TokenUsage? usage)
{
    if (usage is not { } value) return null;
    var result = new Dictionary<string, long> { ["input_tokens"] = value.InputTokens,
        ["cached_input_tokens"] = value.CachedInputTokens, ["output_tokens"] = value.OutputTokens };
    if (value.CacheWriteInputTokens is { } written) result["cache_write_input_tokens"] = written;
    return result;
}
sealed record Fixture(string Name, Step[] Steps);
sealed record Step(double OccurredAt, TokenUsage? LastUsage, TokenUsage? CumulativeUsage);
[StructLayout(LayoutKind.Sequential)]
readonly record struct TrialUsage(long Input, long Cached, long Output, long Written);
[StructLayout(LayoutKind.Sequential)]
readonly record struct TrialState(TrialUsage Usage, double ObservedAt, long HasUsage, long HasTime, long Quality);
[StructLayout(LayoutKind.Sequential)]
readonly record struct TrialObservation(TrialUsage Cumulative, TrialUsage Last, double At, long HasCumulative, long HasLast);
[StructLayout(LayoutKind.Sequential)]
readonly record struct TrialResult(TrialUsage Delta, TrialState State, long HasDelta, long Diagnostic, long Repaired);
static class Native
{
    [DllImport("normalization_trial", EntryPoint = "trial_normalize", CallingConvention = CallingConvention.Cdecl)]
    internal static extern TrialResult Normalize(TrialState state, TrialObservation observation, long retainRepairedZero);
}
