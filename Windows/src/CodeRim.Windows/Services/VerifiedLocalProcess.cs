using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
namespace CodeRim.Windows.Services;

/// <summary>A retained process handle, matching the current user's SID and logon session.</summary>
internal sealed class VerifiedLocalProcess : IDisposable
{
    private readonly SafeProcessHandle handle;
    internal int Id { get; }
    internal string ImagePath { get; }
    private readonly long created;
    private VerifiedLocalProcess(SafeProcessHandle handle, int id, string path, long created)
    { this.handle = handle; Id = id; ImagePath = path; this.created = created; }
    internal static VerifiedLocalProcess Open(int id)
    {
        var handle = OpenProcess(0x1000 | 0x100000, false, id);
        try
        {
            if (handle.IsInvalid || !GetProcessTimes(handle, out var created, out _, out _, out _)) throw new Win32Exception();
            var path = new char[32768]; var length = path.Length;
            if (!QueryFullProcessImageName(handle, 0, path, ref length) || length == 0) throw new Win32Exception();
            if (!OpenProcessToken(handle, 8, out var processToken)) throw new Win32Exception();
            using (processToken)
            using (var owner = new WindowsIdentity(processToken.DangerousGetHandle()))
            using (var current = WindowsIdentity.GetCurrent())
            {
                if (owner.User is null || owner.User != current.User) throw new UnauthorizedAccessException();
                if (!GetTokenInformation(processToken, 12, out var session, sizeof(uint), out _)
                    || !ProcessIdToSessionId(Environment.ProcessId, out var currentSession) || session != currentSession)
                    throw new UnauthorizedAccessException();
            }
            var result = new VerifiedLocalProcess(handle, id, new string(path, 0, length), created);
            if (!result.IsCurrent()) throw new IOException("IDE process changed.");
            return result;
        }
        catch { handle.Dispose(); throw; }
    }
    internal bool IsCurrent() => !handle.IsClosed && !handle.IsInvalid && WaitForSingleObject(handle, 0) == 0x102
        && GetProcessTimes(handle, out var current, out _, out _, out _) && current == created;
    public void Dispose() => handle.Dispose();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exited, out long kernel, out long user);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, [Out] char[] path, ref int length);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information, out uint value, int length, out int returned);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(int id, out uint session);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
