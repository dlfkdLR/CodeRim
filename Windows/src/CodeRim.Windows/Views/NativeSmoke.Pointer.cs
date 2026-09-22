using System.Runtime.InteropServices;
namespace CodeRim.Windows.Views;
internal static partial class NativeSmoke
{
    private static object PointerOwner(IntPtr handle)
    {
        var threadId = WindowProcess(handle, out var pid);
        string? processName = null;
        try { if (pid > 0) { using var process = System.Diagnostics.Process.GetProcessById((int)pid); processName = process.ProcessName; } }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        var characters = new char[256]; var count = WindowClass(handle, characters, characters.Length);
        return new { handlePresent = handle != IntPtr.Zero, ownerResolved = threadId != 0, currentProcess = pid == Environment.ProcessId,
            processName, windowClass = new string(characters, 0, count) };
    }
    private static void RequirePopupClearOfNotch(NotchWindow notch, string context)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(notch).Handle;
        var popupHandle = notch.PopupContent is { } child
            && System.Windows.PresentationSource.FromVisual(child) is System.Windows.Interop.HwndSource source ? source.Handle : IntPtr.Zero;
        Require(WindowBounds(handle, out var bar) && popupHandle != IntPtr.Zero && WindowBounds(popupHandle, out _), "No native popup geometry: " + context);
        WindowBounds(popupHandle, out var popup);
        Require(popup.Right <= bar.Left || popup.Left >= bar.Right || popup.Bottom <= bar.Top || popup.Top >= bar.Bottom,
            "Popup overlaps the native notch after content refresh: " + context);
    }
    private static object PointerWindow(IntPtr handle)
    {
        var exists = WindowBounds(handle, out var bounds);
        return new { present = handle != IntPtr.Zero, rectangleAvailable = exists,
            bounds.Left, bounds.Top, bounds.Right, bounds.Bottom };
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PointerRectangle { internal int Left; internal int Top; internal int Right; internal int Bottom; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetWindowRect", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowBounds(IntPtr handle, out PointerRectangle rectangle);
    [StructLayout(LayoutKind.Sequential)]
    private struct PointerPoint { internal int X; internal int Y; }
#pragma warning disable SYSLIB1054
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SetCursorPos", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveCursor(int x, int y);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint", ExactSpelling = true)]
    private static extern IntPtr WindowAtPoint(PointerPoint point);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", ExactSpelling = true)]
    private static extern uint WindowProcess(IntPtr window, out uint processId);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowClass(IntPtr window, [Out] char[] value, int count);
#pragma warning restore SYSLIB1054
}
