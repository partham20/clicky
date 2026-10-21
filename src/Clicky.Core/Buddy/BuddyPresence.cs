using Microsoft.Extensions.Logging;

namespace Clicky.Core.Buddy;

public sealed class BuddyPresence(ILogger<BuddyPresence> logger) : IBuddyPresence
{
    private int _state = (int)BuddyState.Idle;

    public event EventHandler<BuddyStateChangedEventArgs>? StateChanged;

    public BuddyState State => (BuddyState)Volatile.Read(ref _state);

    public void SetState(BuddyState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown buddy state.");
        }

        var previous = (BuddyState)Interlocked.Exchange(ref _state, (int)state);
        if (previous == state)
        {
            return;
        }

        logger.LogDebug("Buddy {Previous} -> {Current}", previous, state);
        StateChanged?.Invoke(this, new BuddyStateChangedEventArgs(previous, state));
    }
}
