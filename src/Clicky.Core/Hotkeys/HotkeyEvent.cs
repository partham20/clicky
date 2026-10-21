namespace Clicky.Core.Hotkeys;

public enum HotkeyPhase
{
    /// <summary>A hold gesture armed. Always followed by <see cref="Ended"/> or <see cref="Cancelled"/>.</summary>
    Started,

    /// <summary>
    /// A modifier-only hold's keys just went down; it arms (<see cref="Started"/>) after the arming
    /// delay unless another key or a release gets there first (<see cref="Cancelled"/>). For work
    /// that is slow to start and must not miss the beginning, such as opening the microphone. Holds
    /// with a trigger key start at once and have no Pending.
    /// </summary>
    Pending,

    /// <summary>A hold gesture was released normally.</summary>
    Ended,

    /// <summary>
    /// A hold gesture was interrupted, or a <see cref="Pending"/> one never armed: another key was
    /// pressed, a modifier was added or released, the bindings changed, or the session was locked.
    /// Treat as "discard whatever was started".
    /// </summary>
    Cancelled,

    /// <summary>A press or tap gesture completed. One-shot; nothing follows it.</summary>
    Fired,
}

public sealed record HotkeyEvent(HotkeyAction Action, HotkeyPhase Phase, long TimestampMs);
