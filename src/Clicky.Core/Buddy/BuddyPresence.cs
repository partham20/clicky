using Microsoft.Extensions.Logging;

namespace Clicky.Core.Buddy;

public sealed class BuddyPresence(ILogger<BuddyPresence> logger) : IBuddyPresence
{
    private int _state = (int)BuddyState.Idle;
    private float _level;
    private string? _caption;

    public event EventHandler<BuddyStateChangedEventArgs>? StateChanged;

    public event EventHandler? LevelChanged;

    public event EventHandler? CaptionChanged;

    public BuddyState State => (BuddyState)Volatile.Read(ref _state);

    public float Level => Volatile.Read(ref _level);

    public string? Caption => Volatile.Read(ref _caption);

    public void SetLevel(float level)
    {
        level = float.IsFinite(level) ? Math.Clamp(level, 0f, 1f) : 0f;
        if (Interlocked.Exchange(ref _level, level) != level)
        {
            LevelChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetCaption(string? caption)
    {
        caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        if (!string.Equals(Interlocked.Exchange(ref _caption, caption), caption, StringComparison.Ordinal))
        {
            logger.LogDebug("Buddy caption: {Caption}", caption ?? "(none)");
            CaptionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

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
