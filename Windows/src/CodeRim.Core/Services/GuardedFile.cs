namespace CodeRim.Core.Services;

/// <summary>Atomic replacement with version checks. Callers must first require the provider to be quiescent.</summary>
public static class GuardedFile
{
    public static string Read(string path, int maximumBytes = 262144)
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
        output.Position = 0;
        using var reader = new StreamReader(output);
        return reader.ReadToEnd();
    }
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
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var sid = identity.User ?? throw new IOException("The current Windows user is unavailable.");
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            using var stream = System.IO.FileSystemAclExtensions.Create(new FileInfo(path), FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, security);
            using var writer = new StreamWriter(stream); writer.Write(value); writer.Flush(); stream.Flush(true);
        }
        else
        {
            using var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            using var writer = new StreamWriter(stream); writer.Write(value); writer.Flush(); stream.Flush(true);
        }
    }
    private static void Check(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked login locations cannot be switched safely.");
    }
}
