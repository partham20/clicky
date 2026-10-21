using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Clicky.Core.Geometry;
using Microsoft.Extensions.Logging;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Overlay;

/// <summary>
/// Transparent, click-through, never-activated, capture-excluded window covering exactly one
/// monitor. Put content on <see cref="Surface"/> in that monitor's DIPs (origin at its top-left).
/// </summary>
/// <remarks>
/// <para>Geometry is owned in physical pixels: the window is moved onto its monitor while 1x1 px, so
/// WPF handles the DPI change before it has a real size, then sized to the exact bounds. After
/// that, WM_WINDOWPOSCHANGING pins the rect so nothing (WPF's WM_DPICHANGED handling, the shell,
/// a display change) can resize or move it; the host rebuilds overlays on topology changes instead.</para>
/// <para>WS_EX_TRANSPARENT + WS_EX_LAYERED makes every pixel click-through, transparent or not.</para>
/// </remarks>
public sealed class OverlayWindow : Window
{
    private readonly ILogger _logger;

    // Held in a field so the hook delegate lives exactly as long as the window.
    private readonly HwndSourceHook _hook;
    private nint _hwnd;
    private bool _pinned;

    public OverlayWindow(MonitorInfo monitor, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(logger);
        Monitor = monitor;
        _logger = logger;
        _hook = WndProc;

        Title = $"Clicky overlay {monitor.DeviceName}";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;

        Surface = new Canvas { IsHitTestVisible = false, Focusable = false };
        Content = Surface;
    }

    public MonitorInfo Monitor { get; }

    /// <summary>True once closed, including by Application.Shutdown; a closed window cannot be shown or closed again.</summary>
    public bool IsClosed { get; private set; }

    public Canvas Surface { get; }

    /// <summary>Size of <see cref="Surface"/>'s visible area in this monitor's DIPs.</summary>
    public Size SurfaceSize => new(Monitor.Bounds.Width / Monitor.Scale, Monitor.Bounds.Height / Monitor.Scale);

    /// <summary>
    /// Puts the overlay back above topmost windows that appeared since (Task Manager, picture in
    /// picture players). Cheap, but call it on wake-ups rather than every frame.
    /// </summary>
    public void ReassertTopmost()
    {
        if (_hwnd != 0)
        {
            SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLongPtr(_hwnd, GwlExStyle);
        SetWindowLongPtr(_hwnd, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExLayered);

        if (!SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture))
        {
            // Pre-2004 builds, or a remote session that does not support it. The overlay still works,
            // it just shows up in screenshots.
            _logger.LogWarning("Could not exclude overlay on {Monitor} from capture (error {Error})", Monitor.DeviceName, Marshal.GetLastPInvokeError());
        }

        // A topmost window the size of the monitor is what the shell calls a "rude" (full-screen)
        // window: it would turn on Windows' automatic Focus Assist, report the user as busy to every
        // app asking SHQueryUserNotificationState, and make Clicky itself stay quiet. This property is
        // the shell's own opt-out.
        if (!SetProp(_hwnd, "NonRudeHWND", 1))
        {
            _logger.LogWarning("Could not mark overlay on {Monitor} as not full-screen (error {Error})", Monitor.DeviceName, Marshal.GetLastPInvokeError());
        }

        HwndSource.FromHwnd(_hwnd)?.AddHook(_hook);

        var bounds = Monitor.Bounds;
        SetWindowPos(_hwnd, 0, bounds.Left, bounds.Top, 1, 1, SwpNoZOrder | SwpNoActivate);
        SetWindowPos(_hwnd, HwndTopmost, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SwpNoActivate | SwpNoOwnerZOrder);
        _pinned = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        base.OnClosed(e);
    }

    private unsafe nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmWindowPosChanging && _pinned)
        {
            var pos = (WindowPos*)lParam;
            var bounds = Monitor.Bounds;
            if ((pos->Flags & SwpNoMove) == 0)
            {
                pos->X = bounds.Left;
                pos->Y = bounds.Top;
            }

            if ((pos->Flags & SwpNoSize) == 0)
            {
                pos->Cx = bounds.Width;
                pos->Cy = bounds.Height;
            }
        }

        return 0;
    }
}
