using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Capture;

/// <summary>
/// The COM glue Windows.Graphics.Capture needs from a desktop app: a WinRT Direct3D device, capture
/// items for an HMONITOR/HWND, and the Windows 11 borderless switch. Interfaces outside the
/// 19041 projection are called through their vtables so no extra packages are needed.
/// </summary>
internal static class CaptureInterop
{
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidDxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IidD3D10Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    // Windows 11 (build 22000) additions.
    private static readonly Guid IidGraphicsCaptureSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
    private static readonly Guid IidGraphicsCaptureAccessStatics = new("743ED370-06EC-5040-A58A-901F0F757095");
    private static readonly Guid IidAsyncInfo = new("00000036-0000-0000-C000-000000000046");

    private const int GraphicsCaptureAccessKindBorderless = 0;
    private const int AsyncStatusStarted = 0;
    private const int AsyncStatusCompleted = 1;
    private const int AppCapabilityAccessStatusAllowed = 4;

    /// <summary>A hardware D3D11 device (WARP if there is no usable GPU) wrapped for WinRT.</summary>
    public static unsafe IDirect3DDevice CreateDevice(out string description)
    {
        var hr = D3D11CreateDevice(0, D3dDriverTypeHardware, 0, D3d11CreateDeviceBgraSupport, 0, 0, D3d11SdkVersion, out var d3dDevice, out var featureLevel, out var context);
        description = "hardware";
        if (hr < 0)
        {
            hr = D3D11CreateDevice(0, D3dDriverTypeWarp, 0, D3d11CreateDeviceBgraSupport, 0, 0, D3d11SdkVersion, out d3dDevice, out featureLevel, out context);
            description = "WARP";
        }

        Marshal.ThrowExceptionForHR(hr);
        description = $"{description}, feature level {featureLevel >> 12}.{(featureLevel >> 8) & 0xF}";

        try
        {
            // The capture pool and SoftwareBitmap use the immediate context from their own threads.
            if (Marshal.QueryInterface(d3dDevice, in IidD3D10Multithread, out var multithread) >= 0)
            {
                ((delegate* unmanaged[Stdcall]<nint, int, int>)VtableSlot(multithread, 5))(multithread, 1);
                Marshal.Release(multithread);
            }

            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in IidDxgiDevice, out var dxgiDevice));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable));
                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            Marshal.Release(context);
            Marshal.Release(d3dDevice);
        }
    }

    public static GraphicsCaptureItem CreateItemForMonitor(nint monitor) => CreateItem(monitor, slot: 4);

    public static GraphicsCaptureItem CreateItemForWindow(nint hwnd) => CreateItem(hwnd, slot: 3);

    /// <summary>
    /// Windows 11: GraphicsCaptureAccess.RequestAccessAsync(Borderless). Unpackaged apps are allowed
    /// without a prompt. Returns false on Windows 10 or if access was refused.
    /// </summary>
    public static async Task<bool> RequestBorderlessAccessAsync(CancellationToken cancellationToken)
    {
        nint operation;
        try
        {
            operation = StartRequestAccess();
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidCastException or ArgumentException)
        {
            return false;
        }

        if (operation == 0)
        {
            return false;
        }

        try
        {
            // The IAsyncOperation<AppCapabilityAccessStatus> IID is parameterized; polling its
            // non-generic IAsyncInfo avoids computing it. This runs once per process.
            for (var i = 0; i < 200; i++)
            {
                var status = GetAsyncStatus(operation);
                if (status != AsyncStatusStarted)
                {
                    return status == AsyncStatusCompleted && GetAccessResult(operation) == AppCapabilityAccessStatusAllowed;
                }

                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            Marshal.Release(operation);
        }
    }

    /// <summary>Windows 11: GraphicsCaptureSession.IsBorderRequired. Returns false where it does not exist.</summary>
    public static unsafe bool TrySetBorderRequired(GraphicsCaptureSession session, bool required)
    {
        var thisPtr = ((IWinRTObject)session).NativeObject.ThisPtr;
        if (Marshal.QueryInterface(thisPtr, in IidGraphicsCaptureSession3, out var session3) < 0)
        {
            return false;
        }

        try
        {
            // IInspectable (6 slots), get_IsBorderRequired, put_IsBorderRequired.
            return ((delegate* unmanaged[Stdcall]<nint, byte, int>)VtableSlot(session3, 7))(session3, required ? (byte)1 : (byte)0) >= 0;
        }
        finally
        {
            Marshal.Release(session3);
        }
    }

    private static unsafe GraphicsCaptureItem CreateItem(nint handle, int slot)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in IidGraphicsCaptureItemInterop, out var interop));
        try
        {
            nint item;
            var iid = IidGraphicsCaptureItem;

            // IUnknown (3 slots), CreateForWindow, CreateForMonitor.
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)VtableSlot(interop, slot))(interop, handle, &iid, &item));
            try
            {
                return GraphicsCaptureItem.FromAbi(item);
            }
            finally
            {
                Marshal.Release(item);
            }
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    private static unsafe nint StartRequestAccess()
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureAccess");
        if (Marshal.QueryInterface(factory.ThisPtr, in IidGraphicsCaptureAccessStatics, out var statics) < 0)
        {
            return 0;
        }

        try
        {
            // IInspectable (6 slots), RequestAccessAsync.
            nint operation;
            var hr = ((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)VtableSlot(statics, 6))(statics, GraphicsCaptureAccessKindBorderless, &operation);
            return hr >= 0 ? operation : 0;
        }
        finally
        {
            Marshal.Release(statics);
        }
    }

    private static unsafe int GetAsyncStatus(nint operation)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(operation, in IidAsyncInfo, out var info));
        try
        {
            // IInspectable (6 slots), get_Id, get_Status.
            int status;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int*, int>)VtableSlot(info, 7))(info, &status));
            return status;
        }
        finally
        {
            Marshal.Release(info);
        }
    }

    private static unsafe int GetAccessResult(nint operation)
    {
        // IAsyncOperation<T>: IInspectable (6 slots), put_Completed, get_Completed, GetResults.
        int result;
        return ((delegate* unmanaged[Stdcall]<nint, int*, int>)VtableSlot(operation, 8))(operation, &result) >= 0 ? result : -1;
    }

    private static unsafe void* VtableSlot(nint comObject, int slot) => (*(void***)comObject)[slot];
}
