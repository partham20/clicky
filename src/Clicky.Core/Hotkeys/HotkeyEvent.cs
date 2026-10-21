namespace Clicky.Core.Hotkeys;

public enum HotkeyPhase
{
    /// <summary>A hold gesture armed. Always followed by <see cref="Ended"/> or <see cref="Cancelled"/>.</summary>
    Started,

    /// <summary>A hold gesture was released normally.</summary>
    Ended,

    /// <summary>
    /// A hold gesture was interrupted: another key was pressed, a modifier was added, the bindings
    /// changed, or the session was locked. Treat as "discard whatever was started".
    /// </summary>
    Cancelled,

    /// <summary>A press or tap gesture completed. One-shot; nothing follows it.</summary>
    Fired,
}

public sealed record HotkeyEvent(HotkeyAction Action, HotkeyPhase Phase, long TimestampMs);
