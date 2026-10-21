using System.Runtime.InteropServices;

namespace Clicky.Platform.Windows;

// Direct3D 11 device creation and window geometry for Windows.Graphics.Capture.
internal static partial class NativeMethods
{
    internal const int D3dDriverTypeHardware = 1;
    internal const int D3dDriverTypeWarp = 5;
    internal const uint D3d11CreateDeviceBgraSupport = 0x20;
    internal const uint D3d11SdkVersion = 7;

    internal const int DwmwaExtendedFrameBounds = 9;

    [LibraryImport("d3d11.dll")]
    internal static partial int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out int featureLevel,
        out nint immediateContext);

    [LibraryImport("d3d11.dll")]
    internal static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [LibraryImport("dwmapi.dll")]
    internal static unsafe partial int DwmGetWindowAttribute(nint hwnd, int dwAttribute, void* pvAttribute, int cbAttribute);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hWnd);
}
