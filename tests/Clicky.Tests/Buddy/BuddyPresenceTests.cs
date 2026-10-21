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

    [Fact]
    public void Level_is_clamped_and_raised_only_on_change()
    {
        var presence = new BuddyPresence(NullLogger<BuddyPresence>.Instance);
        var raised = 0;
        presence.LevelChanged += (_, _) => raised++;

        presence.SetLevel(0.5f);
        presence.SetLevel(0.5f);
        presence.SetLevel(7f);
        presence.SetLevel(float.NaN);

        Assert.Equal(0f, presence.Level);
        Assert.Equal(3, raised);
        presence.SetLevel(1.5f);
        Assert.Equal(1f, presence.Level);
    }

    [Fact]
    public void Blank_captions_clear_and_repeats_are_ignored()
    {
        var presence = new BuddyPresence(NullLogger<BuddyPresence>.Instance);
        var raised = 0;
        presence.CaptionChanged += (_, _) => raised++;

        presence.SetCaption(" Turn on dark mode ");
        presence.SetCaption("Turn on dark mode");
        Assert.Equal("Turn on dark mode", presence.Caption);

        presence.SetCaption("   ");
        Assert.Null(presence.Caption);
        Assert.Equal(2, raised);
    }
}
