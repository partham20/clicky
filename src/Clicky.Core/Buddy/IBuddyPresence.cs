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
}

public sealed class BuddyStateChangedEventArgs(BuddyState previous, BuddyState current) : EventArgs
{
    public BuddyState Previous { get; } = previous;

    public BuddyState Current { get; } = current;
}
