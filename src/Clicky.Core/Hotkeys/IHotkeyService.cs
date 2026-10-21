namespace Clicky.Core.Hotkeys;

/// <summary>System-wide hotkeys. The Windows implementation is a low-level keyboard hook.</summary>
public interface IHotkeyService
{
    /// <summary>
    /// Raised in order on a single background thread, never on the hook thread. Handlers must not
    /// block for long: later events queue behind them.
    /// </summary>
    event EventHandler<HotkeyEvent>? Triggered;

    /// <summary>Starts listening. Idempotent.</summary>
    void Start();

    /// <summary>Replaces the active bindings (already filtered to enabled ones). Thread-safe.</summary>
    void UpdateBindings(IReadOnlyList<HotkeyBinding> bindings);

    /// <summary>
    /// Stops recognising gestures (keys pass through untouched) until the returned handle is
    /// disposed. Nests: gestures resume when the last handle is gone. An active hold is cancelled.
    /// Thread-safe.
    /// </summary>
    IDisposable Suspend();

    /// <summary>
    /// Shortcut recording: while the returned handle is alive and a Clicky window is in the
    /// foreground, every key event goes to <paramref name="onKey"/> (on the hook thread; marshal it)
    /// and is swallowed, so Win does not open Start and Alt does not open menus. Gestures are suspended
    /// meanwhile. Dispose the handle to stop.
    /// </summary>
    IDisposable BeginRecording(Action<KeyEvent> onKey);
}
