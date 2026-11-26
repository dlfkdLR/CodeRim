using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace CodeRim.WindowCapture;

internal static class NativeCapture
{
    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint hwnd, in Guid iid);
    }

    internal static GraphicsCaptureItem Item(nint hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
        nint item = interop.CreateForWindow(hwnd, in iid);
        try { return GraphicsCaptureItem.FromAbi(item); }
        finally { Marshal.Release(item); }
    }

    internal static IDirect3DDevice Device()
    {
        nint d3d = 0, dxgi = 0, inspectable = 0;
        try
        {
            // The WARP fallback also supports a hosted runner without a hardware adapter.
            int hr = D3D11CreateDevice(0, 1, 0, 32, 0, 0, 7, out d3d, out _, 0);
            if (hr < 0) Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 5, 0, 32, 0, 0, 7, out d3d, out _, 0));
            Guid iid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != 0) Marshal.Release(inspectable);
            if (dxgi != 0) Marshal.Release(dxgi);
            if (d3d != 0) Marshal.Release(d3d);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; internal readonly Rectangle Bounds =>
        Rectangle.FromLTRB(Left, Top, Right, Bottom); }

    internal static (Rectangle Window, Rectangle Extended, Rectangle Client) Bounds(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var window) || !GetClientRect(hwnd, out var client))
            throw new InvalidOperationException("Missing fixture geometry");
        var origin = Point.Empty;
        if (!ClientToScreen(hwnd, ref origin)) throw new InvalidOperationException("Missing fixture origin");
        var framed = DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var extended, 16) >= 0
            ? extended.Bounds : Rectangle.Empty;
        return (window.Bounds, framed, new Rectangle(origin, client.Bounds.Size));
    }

    internal static bool Owned(nint hwnd, int pid) =>
        hwnd != 0 && GetWindowThreadProcessId(hwnd, out uint owner) != 0 && owner == pid;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    internal static extern bool SetProcessDpiAwarenessContext(nint context);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref Point origin);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out Rect value, int size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driver, nint software, uint flags,
        nint levels, uint count, uint sdkVersion, out nint device, out int selected, nint context);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, out nint device);
}
