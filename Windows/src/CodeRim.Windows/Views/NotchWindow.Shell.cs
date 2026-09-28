using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodeRim.Windows.Views;

internal sealed partial class NotchWindow
{
    private static void ExcludeFromWindowSwitcher(IntPtr handle)
    {
        // ShowInTaskbar only controls the taskbar button. An unowned WPF popup
        // still enters Alt+Tab unless it is a tool window. Keep keyboard focus
        // available to explicit accessibility/account-menu navigation.
        const int extendedStyle = -20;
        const long toolWindow = 0x80, appWindow = 0x40000;
        Marshal.SetLastPInvokeError(0);
        var current = ReadExtendedStyle(handle, extendedStyle);
        var readError = Marshal.GetLastPInvokeError();
        if (current == IntPtr.Zero && readError != 0) throw new Win32Exception(readError, "Could not read the notch window style.");
        var style = current.ToInt64();
        Marshal.SetLastPInvokeError(0);
        var previous = WriteExtendedStyle(handle, extendedStyle, new IntPtr((style | toolWindow) & ~appWindow));
        var error = Marshal.GetLastPInvokeError();
        if (previous == IntPtr.Zero && error != 0) throw new Win32Exception(error, "Could not configure the notch tool window.");
        // Commit cached native style data without changing size, placement,
        // z-order or keyboard activation.
        const uint frameChanged = 0x20, noSize = 1, noMove = 2, noZOrder = 4, noActivate = 0x10;
        if (!SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, frameChanged | noSize | noMove | noZOrder | noActivate))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not apply the notch tool window style.");
    }

#pragma warning disable SYSLIB1054
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr ReadExtendedStyle(IntPtr handle, int index);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr WriteExtendedStyle(IntPtr handle, int index, IntPtr value);
#pragma warning restore SYSLIB1054
}
