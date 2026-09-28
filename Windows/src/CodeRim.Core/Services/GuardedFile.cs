using System.Runtime.InteropServices;

namespace CodeRim.Core.Services;

/// <summary>Atomic replacement with version checks. Callers must first require the provider to be quiescent.</summary>
public static class GuardedFile
{
    public static string Read(string path, int maximumBytes = 262144)
        => ReadCore(path, maximumBytes, strictUtf8: false);
    public static string ReadUtf8(string path, int maximumBytes = 262144)
        => ReadCore(path, maximumBytes, strictUtf8: true);
    private static string ReadCore(string path, int maximumBytes, bool strictUtf8)
    {
        Check(path);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximumBytes) throw new InvalidDataException("The login file is too large.");
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = input.Read(buffer)) > 0)
        {
            if (output.Length + count > maximumBytes) throw new InvalidDataException("The login file is too large.");
            output.Write(buffer, 0, count);
        }
        if (strictUtf8)
        {
            var text = new System.Text.UTF8Encoding(false, true).GetString(output.ToArray());
            return text.StartsWith('\uFEFF') ? text[1..] : text;
        }
        output.Position = 0;
        using var reader = new StreamReader(output);
        return reader.ReadToEnd();
    }
    /// <summary>Only a missing leaf is signed out. Missing/linked/inaccessible parents still fail.</summary>
    public static string? ReadIfPresent(string path)
    {
        path = Path.GetFullPath(path);
        using var directory = LoginDirectoryLease.Acquire(path);
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("The login directory is unavailable.");
        Check(parent);
        try { return Read(path); }
        catch (FileNotFoundException)
        {
            // On Windows the retained ancestor handles also prevent directory
            // replacement while distinguishing a missing file from an unsafe path.
            Check(parent); return null;
        }
    }
    /// <summary>Publish a private, complete login only if the destination is still absent.</summary>
    public static void CreateIfAbsent(string path, string value)
    {
        path = Path.GetFullPath(path);
        using var directory = LoginDirectoryLease.Acquire(path);
        if (ReadIfPresent(path) is not null) throw new IOException("The provider changed its login. Nothing was overwritten.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var published = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Retain the private staging handle from creation through commit.
                // A full-path MoveFile reopens the parent for write access and
                // conflicts with the directory lease; rename in that parent instead.
                using var stream = CreatePrivateFile(temporary);
                WriteAndFlush(stream, value);
                WindowsFileRename.InSameDirectory(stream.SafeFileHandle, Path.GetFileName(path));
                published = true;
            }
            else
            {
                WritePrivate(temporary, value);
                PublishWithoutReplacement(temporary, path);
                published = true;
            }
        }
        // No fallible filesystem work after commit: report it as committed even
        // if another process immediately changes directory permissions.
        finally { if (!published) File.Delete(temporary); }
    }
    private static void PublishWithoutReplacement(string source, string destination)
    {
        if (OperatingSystem.IsMacOS())
        {
            // Portable Windows-source probes also run on macOS. The Unix .NET
            // Move(false) implementation can check then rename, replacing a racer.
            // RENAME_EXCL makes absence part of the atomic filesystem operation.
            var utf8 = new System.Text.UTF8Encoding(false, true);
            if (RenameExclusive(utf8.GetBytes(source + "\0"), utf8.GetBytes(destination + "\0"), 4) != 0)
                throw new IOException("The login could not be published without replacement.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
        else throw new PlatformNotSupportedException("Exclusive login publication requires Windows or macOS.");
    }
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameExclusive(byte[] source, byte[] destination, uint flags);
    public static void Replace(string path, string expected, string replacement)
    {
        if (Read(path) != expected) throw new IOException("The provider changed its login. Nothing was overwritten.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WritePrivate(temporary, replacement);
            // Check again after writing the temporary file; do not overwrite a newer CLI login.
            if (Read(path) != expected) throw new IOException("The provider changed its login. Nothing was overwritten.");
            File.Replace(temporary, path, null);
        }
        finally { File.Delete(temporary); }
    }
    public static void WritePrivate(string path, string value)
    {
        using var stream = CreatePrivateFile(path);
        WriteAndFlush(stream, value);
    }
    private static void WriteAndFlush(FileStream stream, string value)
    {
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false, true), 1024, leaveOpen: true);
        writer.Write(value); writer.Flush(); stream.Flush(true);
    }
    private static FileStream CreatePrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var sid = identity.User ?? throw new IOException("The current Windows user is unavailable.");
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            return System.IO.FileSystemAclExtensions.Create(new FileInfo(path), FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, security);
        }
        else
        {
            return new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        }
    }
    private static void Check(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked login locations cannot be switched safely.");
    }
}
