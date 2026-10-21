namespace Clicky.Core.Buddy;

/// <summary>What the buddy is doing, shown by its animation.</summary>
public enum BuddyState
{
    /// <summary>Waiting. Static art, so no frames are rendered.</summary>
    Idle,

    /// <summary>Talk hotkey is held and the mic is open.</summary>
    Listening,

    /// <summary>Request sent; waiting for the first token or action.</summary>
    Thinking,

    /// <summary>Reply is being spoken.</summary>
    Speaking,
}
