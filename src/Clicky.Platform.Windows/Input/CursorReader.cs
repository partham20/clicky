using Clicky.Core.Geometry;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Input;

public static class CursorReader
{
    /// <summary>
    /// Pointer hotspot in physical pixels, and whether the system cursor is drawn (games, video
    /// players and touch input hide it). Returns false when the position is unavailable, e.g. while
    /// the secure desktop (UAC, Ctrl+Alt+Del) is up.
    /// </summary>
    public static bool TryGet(out PixelPoint position, out bool isVisible)
    {
        var info = new CursorInfo { Size = System.Runtime.CompilerServices.Unsafe.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info))
        {
            position = default;
            isVisible = false;
            return false;
        }

        position = new PixelPoint(info.ScreenPosition.X, info.ScreenPosition.Y);
        isVisible = (info.Flags & CursorShowing) != 0;
        return true;
    }
}
