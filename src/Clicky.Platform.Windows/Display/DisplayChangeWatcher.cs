using System.Windows.Interop;
using System.Windows.Threading;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Display;

/// <summary>
/// Raises <see cref="Changed"/> (debounced, on the UI thread) when monitors may have been added,
/// removed, moved, rescaled or had their work area change. Listeners re-enumerate and compare, so
/// an occasional spurious event is harmless.
/// </summary>
/// <remarks>
/// Uses its own hidden top-level window: WM_DISPLAYCHANGE and WM_SETTINGCHANGE are broadcast only
/// to top-level windows, and the overlays may not exist (buddy docked) when the topology changes.
/// Create and dispose on the UI thread.
/// </remarks>
public sealed class DisplayChangeWatcher : IDisposable
{
    private readonly HwndSource _source;
    private readonly HwndSourceHook _hook;
    private readonly DispatcherTimer _debounce;

    public DisplayChangeWatcher(Dispatcher dispatcher, TimeSpan debounce)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        _source = new HwndSource(new HwndSourceParameters("Clicky display watcher")
        {
            Width = 0,
            Height = 0,
            WindowStyle = WsPopup,
            ExtendedWindowStyle = (int)WsExToolWindow,
        });
        _hook = WndProc;
        _source.AddHook(_hook);

        _debounce = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = debounce };
        _debounce.Tick += OnDebounce;
    }

    public event EventHandler? Changed;

    /// <summary>Report a change seen elsewhere (e.g. WM_DPICHANGED on an overlay), coalesced with the rest.</summary>
    public void Poke()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void Dispose()
    {
        _debounce.Stop();
        _debounce.Tick -= OnDebounce;
        _source.RemoveHook(_hook);
        _source.Dispose();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg is WmDisplayChange or WmSettingChange or WmDpiChanged)
        {
            Poke();
        }

        return 0;
    }

    private void OnDebounce(object? sender, EventArgs e)
    {
        _debounce.Stop();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
