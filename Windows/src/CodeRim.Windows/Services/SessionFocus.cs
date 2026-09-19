using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CodeRim.Core.Services;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CodeRim.Windows.Services;

/// <summary>Explicit-click handoff only. Never selects a terminal tab or forces foreground permission.</summary>
internal static class SessionFocus
{
    internal static bool Activate(SessionActivity session)
    {
        try
        {
            if (session.CodexThreadUri is { } thread)
            {
                using var protocol = Registry.ClassesRoot.OpenSubKey("codex");
                if (protocol?.GetValue("URL Protocol") is null) return false;
                using var launched = Process.Start(new ProcessStartInfo(thread.AbsoluteUri) { UseShellExecute = true });
                return true;
            }
            var window = FindOwningWindow(session);
            if (window == IntPtr.Zero) return false;
            if (IsIconic(window)) _ = ShowWindow(window, 9);
            return SetForegroundWindow(window);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException
            or System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    internal static IntPtr FindOwningWindow(SessionActivity session)
    {
        if (session.ProcessId is not > 1 || session.ProcessStartedAt is not { } expectedStart) return IntPtr.Zero;
        try
        {
            using var original = Process.GetProcessById(session.ProcessId.Value);
            var childStart = original.StartTime.ToUniversalTime();
            if (original.HasExited || Math.Abs((childStart - expectedStart.UtcDateTime).TotalSeconds) > 1) return IntPtr.Zero;
            var parents = new Dictionary<uint, uint>();
            using var snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot.IsInvalid) return IntPtr.Zero;
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
            if (!Process32FirstW(snapshot, ref entry)) return IntPtr.Zero;
            do { parents[entry.ProcessId] = entry.ParentProcessId; }
            while (parents.Count < 100000 && Process32NextW(snapshot, ref entry));
            var seen = new HashSet<uint>();
            var pid = (uint)session.ProcessId.Value;
            for (var depth = 0; depth < 8 && pid > 1 && seen.Add(pid); depth++)
            {
                using var process = Process.GetProcessById((int)pid);
                var created = process.StartTime.ToUniversalTime();
                if (process.HasExited || created > childStart) break; // Do not follow a reused parent PID.
                childStart = created;
                var found = IntPtr.Zero;
                _ = EnumWindows((window, parameter) =>
                {
                    _ = GetWindowThreadProcessId(window, out var owner);
                    if (owner != pid || !IsWindowVisible(window)) return true;
                    found = window; return false;
                }, IntPtr.Zero);
                if (found != IntPtr.Zero) return found;
                if (!parents.TryGetValue(pid, out pid)) break;
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException) { }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, ProcessId;
        internal UIntPtr DefaultHeapId;
        internal uint ModuleId, Threads, ParentProcessId;
        internal int BasePriority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ExeFile;
    }
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
#pragma warning disable SYSLIB1054
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
#pragma warning restore SYSLIB1054
}
