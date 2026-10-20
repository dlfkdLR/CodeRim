namespace CodeRim.Core.Services;

/// <summary>Use extended paths only at Win32 boundaries; retain ordinary paths for identity comparisons.</summary>
internal static class WindowsNativePath
{
    internal static string ForApi(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
        if (fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new IOException("Device namespace paths are not supported.");
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
    }

    internal static string FromApi(string path)
        => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
}
