using Clicky.Core.Buddy;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Buddy;

public sealed class BuddyPresenceTests
{
    [Fact]
    public void Starts_idle()
    {
        Assert.Equal(BuddyState.Idle, new BuddyPresence(NullLogger<BuddyPresence>.Instance).State);
    }

    [Fact]
    public void Raises_only_on_real_changes()
    {
        var presence = new BuddyPresence(NullLogger<BuddyPresence>.Instance);
        var seen = new List<(BuddyState, BuddyState)>();
        presence.StateChanged += (_, e) => seen.Add((e.Previous, e.Current));

        presence.SetState(BuddyState.Listening);
        presence.SetState(BuddyState.Listening);
        presence.SetState(BuddyState.Thinking);
        presence.SetState(BuddyState.Idle);

        Assert.Equal(
            [(BuddyState.Idle, BuddyState.Listening), (BuddyState.Listening, BuddyState.Thinking), (BuddyState.Thinking, BuddyState.Idle)],
            seen);
    }

    [Fact]
    public void Rejects_undefined_states()
    {
        var presence = new BuddyPresence(NullLogger<BuddyPresence>.Instance);

        Assert.Throws<ArgumentOutOfRangeException>(() => presence.SetState((BuddyState)42));
        Assert.Equal(BuddyState.Idle, presence.State);
    }
}
