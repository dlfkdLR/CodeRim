using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CodeRim.Core.Domain;

internal static class RustAccountingLibrary
{
    internal const string BridgeName = "coderim_accounting_bridge";
    static RustAccountingLibrary() => NativeLibrary.SetDllImportResolver(typeof(RustAccountingLibrary).Assembly, ResolveLibrary);
    internal static void EnsureResolverInstalled() { }

    // The full transition uses 64-bit scalar fields. Its flags preserve null versus zero.
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct RawUsage(long Input, long Cached, long Output, long Written, long HasWritten)
    {
        internal static RawUsage From(TokenUsage? usage) => usage is { } value
            ? new(value.InputTokens, value.CachedInputTokens, value.OutputTokens,
                value.CacheWriteInputTokens.GetValueOrDefault(), value.CacheWriteInputTokens.HasValue ? 1 : 0)
            : default;

        internal TokenUsage ToUsage() => new(Input, Cached, Output, HasWritten == 0 ? null : Written);
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

        if (!RustAccountingLoadState.IsLoaded)
        {
            // Core can query this marker before loading the usage assembly on an update-only launch.
            AppContext.SetData(RustAccountingLoadState.AppContextKey, true);
            RustAccountingLoadState.IsLoaded = true;
        }
        return handle;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint LoadLibraryExW(string filename, nint file, uint flags);
}

// Read by the activity timer without initializing the resolver or loading a library.
internal static class RustAccountingLoadState
{
    internal const string AppContextKey = "CodeRim.UsageCore.RustAccountingLibrary.Loaded";
    internal static volatile bool IsLoaded;
}
