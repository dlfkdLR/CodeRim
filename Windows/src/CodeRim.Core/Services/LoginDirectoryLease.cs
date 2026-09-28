using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeRim.Core.Services;

/// <summary>Pin each Windows ancestor before traversing it; never follow a reparse point.</summary>
internal sealed class LoginDirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    private LoginDirectoryLease() { }
    internal static LoginDirectoryLease Acquire(string file)
    {
        var lease = new LoginDirectoryLease();
        if (!OperatingSystem.IsWindows()) return lease; // Portable fixtures retain GuardedFile's path checks.
        try
        {
            var ancestors = new Stack<string>();
            for (var path = Path.GetDirectoryName(Path.GetFullPath(file)); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
                ancestors.Push(path);
            while (ancestors.TryPop(out var path))
            {
                // READ_ATTRIBUTES, SHARE_READ, OPEN_EXISTING, BACKUP_SEMANTICS |
                // OPEN_REPARSE_POINT. Deny write/delete handles for the lifetime
                // of publication, including attempts to replace an ancestor.
                var handle = CreateFile(path, 0x80, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var code = Marshal.GetLastWin32Error(); handle.Dispose();
                    throw new IOException("The login directory could not be secured.", new Win32Exception(code));
                }
                lease.handles.Add(handle);
                if (!GetFileInformationByHandleEx(handle, 9, out var info, 8))
                    throw new IOException("The login directory could not be inspected.", new Win32Exception(Marshal.GetLastWin32Error()));
                if ((info.Attributes & FileAttributes.Directory) == 0 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked login locations cannot be switched safely.");
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    public void Dispose()
    {
        for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
        handles.Clear();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { internal FileAttributes Attributes; internal uint ReparseTag; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeTagInfo info, uint size);
}
