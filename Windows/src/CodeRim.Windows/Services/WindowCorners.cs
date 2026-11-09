using System.Runtime.InteropServices;

namespace CodeRim.Windows.Services;

/// <summary>
/// Restore DWM corners removed by WindowChrome; older Windows versions ignore the preference.
/// </summary>
internal static class WindowCorners
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    internal static void Round(nint window)
    {
        if (window == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var preference = DwmwcpRound;
        try { _ = DwmSetWindowAttribute(window, DwmwaWindowCornerPreference, ref preference, sizeof(int)); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

#pragma warning disable SYSLIB1054 // The project does not enable unsafe code for LibraryImport.
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
#pragma warning restore SYSLIB1054
}
