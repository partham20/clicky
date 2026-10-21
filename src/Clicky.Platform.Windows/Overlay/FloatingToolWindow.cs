using System.Runtime.InteropServices;
using Clicky.Core.Geometry;
using Microsoft.Extensions.Logging;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Overlay;

/// <summary>
/// For small clickable windows that float over everything (the reply card): never take focus from
/// the app the user is in, stay out of Alt+Tab and the taskbar, and stay out of screenshots and
/// screen shares like the buddy's overlays. Unlike the overlays they do get mouse clicks.
/// </summary>
public static class FloatingToolWindow
{
    /// <summary>Call from the window's SourceInitialized.</summary>
    /// <param name="clickThrough">Mouse input passes through to the window below (captions that are only read).</param>
    /// <param name="activatable">The window takes focus when clicked or shown (text mode's box, which is typed into).</param>
    public static void Apply(nint hwnd, ILogger logger, bool clickThrough = false, bool activatable = false)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle) | WsExToolWindow | (activatable ? 0 : WsExNoActivate);
        if (clickThrough)
        {
            exStyle |= WsExTransparent | WsExLayered;
        }

        SetWindowLongPtr(hwnd, GwlExStyle, exStyle);
        if (!SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture))
        {
            logger.LogDebug("Could not exclude a floating window from capture (error {Error})", Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Moves the window's top-left to a point in physical pixels, above other topmost windows, without activating it.</summary>
    public static void MoveTo(nint hwnd, PixelPoint topLeft) =>
        SetWindowPos(hwnd, HwndTopmost, topLeft.X, topLeft.Y, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
}
