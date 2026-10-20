using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace CodeRim.Core.Services;

/// <summary>Publish an open private staging file without reopening its pinned parent for write access.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFileRename
{
    internal static void InSameDirectory(SafeFileHandle source, string name)
    {
        // Native FileRenameInformation: NULL RootDirectory plus a simple name
        // refers to the source handle's parent, independent of the working directory.
        // A full path (including Win32 MoveFile) reopens that parent for FILE_ADD_FILE,
        // which conflicts with our retained deny-write/reparse directory lease.
        if (name.Length == 0 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.') ||
            name.IndexOfAny(['\\', '/', ':', '\0']) >= 0)
            throw new IOException("The login filename cannot be published safely.");
        var encoded = new System.Text.UnicodeEncoding(false, false, true).GetBytes(name);
        // BOOLEAN/ULONG union; pointer-aligned HANDLE; ULONG byte length; WCHAR[].
        // Zero flags keep ReplaceIfExists false, so an external sign-in always wins.
        var nameOffset = IntPtr.Size == 8 ? 20 : 12;
        var information = new byte[checked(nameOffset + encoded.Length + 2)];
        BinaryPrimitives.WriteUInt32LittleEndian(information.AsSpan(nameOffset - 4), checked((uint)encoded.Length));
        encoded.CopyTo(information, nameOffset);
        // The private FileStream is synchronous and has DELETE access. Keep it open
        // until this rename completes; do not reopen or follow a replaced staging path.
        var status = NtSetInformationFile(source, out _, information, checked((uint)information.Length), 10);
        if (status != 0)
            throw new IOException("The login could not be published without replacement.",
                new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { internal IntPtr Status; internal UIntPtr Information; }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock status, byte[] information, uint length, int informationClass);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
