namespace Clicky.Core.Buddy;

/// <summary>
/// The buddy's current state, shared by everything that shows it (the floating overlay buddy and
/// the docked one in Home). Voice in/out and the talk loop (M3, M4, M6) drive it.
/// </summary>
public interface IBuddyPresence
{
    /// <summary>Safe to read from any thread.</summary>
    BuddyState State { get; }

    /// <summary>Raised on the thread that called <see cref="SetState"/>, only when the state actually changes.</summary>
    event EventHandler<BuddyStateChangedEventArgs>? StateChanged;

    void SetState(BuddyState state);

    /// <summary>Microphone level 0..1 while listening, voice level while speaking, else 0. Safe to read from any thread.</summary>
    float Level { get; }

    /// <summary>Raised on the calling thread, up to ~20 times a second while listening or speaking.</summary>
    event EventHandler? LevelChanged;

    /// <summary>Clamped to 0..1.</summary>
    void SetLevel(float level);

    /// <summary>A short line shown next to the buddy (what it heard, or why it could not), or null.</summary>
    string? Caption { get; }

    /// <summary>Raised on the calling thread when <see cref="Caption"/> changes.</summary>
    event EventHandler? CaptionChanged;

    void SetCaption(string? caption);
}

public sealed class BuddyStateChangedEventArgs(BuddyState previous, BuddyState current) : EventArgs
{
    public BuddyState Previous { get; } = previous;

    public BuddyState Current { get; } = current;
}
