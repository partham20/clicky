using System.Runtime.InteropServices;
using Clicky.Core.Geometry;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Display;

public static class DisplayMonitors
{
    /// <summary>
    /// Current monitors in physical pixels, primary first, then left to right. The process is
    /// PerMonitorV2, so nothing here is DPI-virtualized.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> GetAll() => [.. GetAllWithHandles().Select(m => m.Info)];

    /// <summary>Same order as <see cref="GetAll"/>, with each monitor's HMONITOR (valid until the topology changes).</summary>
    internal static IReadOnlyList<(nint Handle, MonitorInfo Info)> GetAllWithHandles()
    {
        var monitors = new List<(nint Handle, MonitorInfo Info)>();
        var handle = GCHandle.Alloc(monitors);
        try
        {
            unsafe
            {
                EnumDisplayMonitors(0, 0, &OnMonitor, GCHandle.ToIntPtr(handle));
            }
        }
        finally
        {
            handle.Free();
        }

        return [.. monitors.OrderByDescending(m => m.Info.IsPrimary).ThenBy(m => m.Info.Bounds.Left).ThenBy(m => m.Info.Bounds.Top)];
    }

    [UnmanagedCallersOnly]
    private static unsafe int OnMonitor(nint monitor, nint hdc, Rect* clip, nint data)
    {
        // An exception escaping an UnmanagedCallersOnly method kills the process.
        try
        {
            var info = new MonitorInfoEx { Size = sizeof(MonitorInfoEx) };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return 1;
            }

            // MDT_EFFECTIVE_DPI is the user's scale setting; fall back to 100% if shcore refuses.
            var scale = GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;

            var list = (List<(nint, MonitorInfo)>)GCHandle.FromIntPtr(data).Target!;
            list.Add((monitor, new MonitorInfo(
                new string(info.Device),
                ToPixelRect(info.Monitor),
                ToPixelRect(info.Work),
                scale,
                (info.Flags & MonitorInfoPrimary) != 0)));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException)
        {
            // Skip a monitor we could not describe rather than lose the whole list.
        }

        return 1;
    }

    private static PixelRect ToPixelRect(Rect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
