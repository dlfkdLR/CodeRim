using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeRim.Core.Services;

internal readonly record struct SourceFileIdentity(uint VolumeSerialNumber, ulong FileIndex)
{
    public static SourceFileIdentity? TryRead(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows() || !GetFileInformationByHandle(handle, out var information))
            return null;
        return new SourceFileIdentity(information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    public static SourceFileIdentity? TryRead(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);
            return TryRead(handle);
        }
        catch (Exception error) when (error is IOException
                                      or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            return null;
        }
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    #pragma warning disable SYSLIB1054 // This small blittable Win32 call avoids enabling unsafe source-generated interop.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);
    #pragma warning restore SYSLIB1054
}
