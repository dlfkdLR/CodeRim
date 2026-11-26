using System.Runtime.InteropServices;
#if CODERIM_RUST_ACCOUNTING
using CodeRim.Core.Domain;
#endif

namespace CodeRim.Core.Services;

internal enum DevicePollAction { Refused, Wait, Store, Expired, Denied, Overflow }

internal static class GitHubDevicePolicy
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Schedule(int Prompt, int IntervalSeconds, int ValidSeconds);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Poll(long IntervalTicks, DevicePollAction Action);

    internal static Schedule Prepare(bool hasUserCode, bool hasDeviceCode, bool hasHttpsUri, int? interval, int? valid)
    {
#if CODERIM_RUST_ACCOUNTING
        byte presence = (byte)((hasUserCode ? 1 : 0) | (hasDeviceCode ? 2 : 0) | (hasHttpsUri ? 4 : 0));
        return NativeSchedule(presence, interval ?? 5, valid ?? 900);
#else
        return new(hasUserCode && hasDeviceCode && hasHttpsUri ? 1 : 0,
            Math.Clamp(interval ?? 5, 1, 60), Math.Clamp(valid ?? 900, 60, 1800));
#endif
    }

    internal static Poll Next(bool approved, string? error, TimeSpan interval)
    {
#if CODERIM_RUST_ACCOUNTING
        // The fixed provider error vocabulary is the only response detail that crosses the ABI.
        // Unknown text and all codes and tokens stay in the managed process.
        byte kind = error switch { "authorization_pending" => 1, "slow_down" => 2,
            "expired_token" => 3, "access_denied" => 4, _ => 0 };
        var result = NativePoll(approved ? (byte)1 : (byte)0, kind, interval.Ticks);
        if (result.Action == DevicePollAction.Overflow) throw new OverflowException();
        return result;
#else
        if (approved) return new(interval.Ticks, DevicePollAction.Store);
        return error switch
        {
            "authorization_pending" => new(interval.Ticks, DevicePollAction.Wait),
            "slow_down" => new((interval + TimeSpan.FromSeconds(5)).Ticks, DevicePollAction.Wait),
            "expired_token" => new(interval.Ticks, DevicePollAction.Expired),
            "access_denied" => new(interval.Ticks, DevicePollAction.Denied),
            _ => new(interval.Ticks, DevicePollAction.Refused),
        };
#endif
    }

#if CODERIM_RUST_ACCOUNTING
    static GitHubDevicePolicy() => RustAccountingLibrary.EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_github_device_schedule", ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    private static extern Schedule NativeSchedule(byte presence, int interval, int valid);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_github_device_poll", ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    private static extern Poll NativePoll(byte approved, byte error, long intervalTicks);
#endif
}
