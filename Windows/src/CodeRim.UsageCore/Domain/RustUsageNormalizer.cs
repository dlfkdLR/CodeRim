using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CodeRim.Core.Domain;

internal static class RustUsageNormalizer
{
    // All ABI fields are signed 64-bit integers. Separate flags preserve null versus zero.
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RawUsage(long Input, long Cached, long Output, long Written, long HasWritten)
    {
        internal static RawUsage From(TokenUsage? usage) => usage is { } value
            ? new(value.InputTokens, value.CachedInputTokens, value.OutputTokens,
                value.CacheWriteInputTokens.GetValueOrDefault(), value.CacheWriteInputTokens.HasValue ? 1 : 0)
            : default;

        internal TokenUsage ToUsage() => new(Input, Cached, Output, HasWritten == 0 ? null : Written);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RawState(RawUsage HighWater, long At, long HasHighWater, long HasAt, long Quality);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RawObservation(RawUsage Cumulative, RawUsage Last, long At, long HasCumulative, long HasLast);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RawResult(RawState State, RawUsage Delta, long HasDelta, long Diagnostic, long Repaired);

    private const string BridgeName = "coderim_accounting_bridge";

    static RustUsageNormalizer() => NativeLibrary.SetDllImportResolver(typeof(RustUsageNormalizer).Assembly, ResolveLibrary);

    // This leaf transition performs bounded fixed-width scalar arithmetic only. It cannot allocate,
    // block, call managed code, or retain an argument. Revisit this annotation if that contract changes.
    [SuppressGCTransition]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(BridgeName, EntryPoint = "cr_normalize_windows", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern RawResult Transition(RawState state, RawObservation observation);

    internal static UsageNormalizationResult Normalize(TokenObservation observation, in UsageNormalizationState state)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var prior = new RawState(RawUsage.From(state.CumulativeHighWaterMark), state.LastObservedAt?.UtcTicks ?? 0,
            state.CumulativeHighWaterMark.HasValue ? 1 : 0, state.LastObservedAt.HasValue ? 1 : 0, (long)state.Quality);
        var sample = new RawObservation(RawUsage.From(observation.CumulativeUsage), RawUsage.From(observation.LastUsage),
            observation.OccurredAt.UtcTicks, observation.CumulativeUsage.HasValue ? 1 : 0, observation.LastUsage.HasValue ? 1 : 0);
        var result = Transition(prior, sample);

        // Rejected observations retain the prior offset even when both instants have identical UTC ticks.
        DateTimeOffset? lastObserved = null;
        if (result.State.HasAt != 0)
        {
            var offset = result.Diagnostic is 1 or 2 ? state.LastObservedAt!.Value.Offset : observation.OccurredAt.Offset;
            lastObserved = new DateTimeOffset(result.State.At, TimeSpan.Zero).ToOffset(offset);
        }
        var quality = (DataQuality)result.State.Quality;
        // An opaque quality from a newer caller survives a transition that retained the input quality.
        if (quality == DataQuality.Error && !Enum.IsDefined(state.Quality)) quality = state.Quality;
        var next = new UsageNormalizationState(result.State.HasHighWater == 0 ? null : result.State.HighWater.ToUsage(),
            lastObserved, quality);
        return new UsageNormalizationResult(result.HasDelta == 0 ? null : result.Delta.ToUsage(), next, result.Diagnostic switch
        {
            0 => null,
            1 => "out-of-order token snapshot ignored",
            2 => "missing cumulative token usage",
            3 => "initial cumulative baseline is unresolved",
            4 => "cumulative counter restarted",
            5 => "cumulative token usage decreased or interleaved",
            6 => "inconsistent token delta clamped",
            7 => "inconsistent token usage clamped",
            8 => "cumulative token difference overflowed; baseline reset",
            _ => throw new InvalidDataException("Unrecognized Rust accounting diagnostic.")
        }) { Repaired = result.Repaired != 0 };
    }

    private static nint ResolveLibrary(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // kernel32.dll has its own System32-only import below.
        if (name != BridgeName) return 0;
        var filename = OperatingSystem.IsWindows() ? "coderim_accounting_bridge.dll"
            : OperatingSystem.IsMacOS() ? "libcoderim_accounting_bridge.dylib" : "libcoderim_accounting_bridge.so";
        var path = Path.Combine(AppContext.BaseDirectory, filename);
        if (!Path.IsPathFullyQualified(path)) throw new DllNotFoundException("Missing absolute application directory.");

        // The Windows bridge may resolve OS dependencies only from System32. The main library
        // is loaded from its exact installed filename, independent of PATH and the working directory.
        const uint loadLibrarySearchSystem32 = 0x800;
        nint handle;
        if (OperatingSystem.IsWindows())
        {
            handle = LoadLibraryExW(path, 0, loadLibrarySearchSystem32);
            if (handle == 0)
                throw new DllNotFoundException($"Could not load {filename} from the application directory.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        else
        {
            handle = NativeLibrary.Load(path);
        }

        return handle;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint LoadLibraryExW(string filename, nint file, uint flags);
}
