using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace CodeRim.Core.Services;

[SupportedOSPlatform("windows")]
internal static class WindowsApplicationRestart
{
    // Only the fully authenticated normal application receives the user's ordinary environment.
    // No value from this block is logged, persisted or passed to the trust/transaction worker.
    internal static void Start(string application, string directory)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x000a, out var token)) throw new System.ComponentModel.Win32Exception();
        using (token)
        {
            if (!CreateEnvironmentBlock(out var block, token, false)) throw new System.ComponentModel.Win32Exception();
            try
            {
                var environment = ReadBlock(block);
                var start = new ProcessStartInfo(application) { UseShellExecute = false, WorkingDirectory = directory };
                start.Environment.Clear(); foreach (var value in environment) start.Environment[value.Key] = value.Value;
                lock (BoundedProcess.CreationLock) { using var process = Process.Start(start) ?? throw new IOException("The updated application could not restart."); }
            }
            finally { _ = DestroyEnvironmentBlock(block); }
        }
    }
    private static Dictionary<string, string?> ReadBlock(nint block)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase); var offset = 0;
        while (offset < 262144)
        {
            var begin = offset;
            while (offset < 262144 && Marshal.ReadInt16(block, offset * 2) != 0) offset++;
            if (offset >= 262144) throw new InvalidDataException("The application environment exceeds its limit.");
            if (begin == offset) return result;
            var text = Marshal.PtrToStringUni(block + begin * 2, offset - begin)!; offset++;
            var equals = text.IndexOf('='); if (equals <= 0) continue; // Per-drive pseudo-variables are unnecessary for an absolute executable/working directory.
            result[text[..equals]] = text[(equals + 1)..];
        }
        throw new InvalidDataException("The application environment exceeds its limit.");
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern nint GetCurrentProcess();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("userenv.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateEnvironmentBlock(out nint block, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("userenv.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyEnvironmentBlock(nint block);
}
