using Clicky.Core.Settings;
using Clicky.Core.Suggestions;
using Clicky.Core.Voice;

namespace Clicky.Tests.Suggestions;

public sealed class SuggestionPlannerTests
{
    private static readonly SuggestionSettings Settings = new();
    private static readonly DateOnly Monday = new(2026, 10, 19);
    private static readonly SuggestionPresence AtDesk = new(TimeSpan.FromSeconds(5), QuietReasons.None, Busy: false);

    [Fact]
    public void Waits_before_the_research_window_and_after_the_last_hour()
    {
        Assert.Equal(MorningStep.Wait, Next(At(Monday, 4, 59), []));
        Assert.Equal(MorningStep.Wait, Next(At(Monday, 12, 0), []));
        Assert.Equal(MorningStep.Wait, Next(At(Monday, 23, 0), []));
    }

    [Fact]
    public void Researches_an_hour_before_the_card_is_due()
    {
        Assert.Equal(MorningStep.Research, Next(At(Monday, 5, 0), []));
        Assert.Equal(MorningStep.Research, Next(At(Monday, 9, 30), [new MorningRecord(Monday.AddDays(-1), Researched: true)]));
    }

    [Fact]
    public void Shows_from_six_once_researched_and_the_user_is_at_the_pc()
    {
        MorningRecord[] mornings = [new(Monday, Researched: true)];
        Assert.Equal(MorningStep.Wait, Next(At(Monday, 5, 30), mornings));
        Assert.Equal(MorningStep.Show, Next(At(Monday, 6, 0), mornings));
        Assert.Equal(MorningStep.Wait, Next(At(Monday, 6, 0), mornings, hasIdeas: false));
    }

    [Fact]
    public void Shows_once_a_day()
    {
        MorningRecord[] mornings = [new(Monday, At(Monday, 6, 5), Researched: true)];
        Assert.Equal(MorningStep.Wait, Next(At(Monday, 7, 0), mornings));
    }

    [Theory]
    [InlineData(QuietReasons.InCall)]
    [InlineData(QuietReasons.ScreenSharing)]
    [InlineData(QuietReasons.FocusAssist)]
    [InlineData(QuietReasons.FullScreen)]
    public void Holds_the_card_during_calls_sharing_focus_assist_and_full_screen(QuietReasons reason)
    {
        MorningRecord[] mornings = [new(Monday, Researched: true)];
        Assert.Equal(MorningStep.Wait, Next(At(Monday, 8, 0), mornings, presence: AtDesk with { Quiet = reason }));
    }

    [Theory]
    [InlineData(QuietReasons.Muted)]
    [InlineData(QuietReasons.QuietMode)]
    [InlineData(QuietReasons.NoOutputDevice)]
    public void A_silent_card_is_not_held_by_sound_conditions(QuietReasons reason)
    {
        MorningRecord[] mornings = [new(Monday, Researched: true)];
        Assert.Equal(MorningStep.Show, Next(At(Monday, 8, 0), mornings, presence: AtDesk with { Quiet = reason }));
    }

    [Fact]
    public void Holds_the_card_while_the_user_is_away_or_talking_to_clicky()
    {
        MorningRecord[] mornings = [new(Monday, Researched: true)];
        Assert.Equal(MorningStep.Wait, Next(At(Monday, 8, 0), mornings, presence: AtDesk with { Idle = TimeSpan.FromMinutes(10) }));
        Assert.Equal(MorningStep.Wait, Next(At(Monday, 8, 0), mornings, presence: AtDesk with { Busy = true }));
    }

    [Fact]
    public void Presence_is_only_checked_when_everything_else_says_show()
    {
        var checks = 0;
        SuggestionPresence Count()
        {
            checks++;
            return AtDesk;
        }

        SuggestionPlanner.Next(At(Monday, 3, 0), Settings, [], true, Count);
        SuggestionPlanner.Next(At(Monday, 5, 30), Settings, [], true, Count);
        Assert.Equal(0, checks);
        SuggestionPlanner.Next(At(Monday, 7, 0), Settings, [new(Monday, Researched: true)], true, Count);
        Assert.Equal(1, checks);
    }

    [Fact]
    public void Disabled_never_researches_or_shows()
    {
        Assert.Equal(MorningStep.Wait, SuggestionPlanner.Next(At(Monday, 8, 0), Settings with { Enabled = false }, [], true, () => AtDesk));
    }

    [Fact]
    public void Honours_custom_hours()
    {
        var late = new SuggestionSettings(ShowAfterHour: 9, LastHour: 11).Normalize();
        MorningRecord[] mornings = [new(Monday, Researched: true)];
        Assert.Equal(MorningStep.Wait, SuggestionPlanner.Next(At(Monday, 7, 30), late, [], true, () => AtDesk));
        Assert.Equal(MorningStep.Research, SuggestionPlanner.Next(At(Monday, 8, 0), late, [], true, () => AtDesk));
        Assert.Equal(MorningStep.Wait, SuggestionPlanner.Next(At(Monday, 8, 30), late, mornings, true, () => AtDesk));
        Assert.Equal(MorningStep.Show, SuggestionPlanner.Next(At(Monday, 9, 0), late, mornings, true, () => AtDesk));
        Assert.Equal(MorningStep.Wait, SuggestionPlanner.Next(At(Monday, 11, 0), late, mornings, true, () => AtDesk));
    }

    [Fact]
    public void Backs_off_after_three_ignored_mornings_and_comes_back_when_the_user_reacts()
    {
        var mornings = new List<MorningRecord>();
        for (var i = 0; i < 3; i++)
        {
            mornings.Add(Ignored(Monday.AddDays(i)));
        }

        // Three ignored (Mon-Wed): Thursday is skipped, Friday comes back.
        var thursday = Monday.AddDays(3);
        Assert.Equal(3, SuggestionPlanner.IgnoredInARow(mornings, thursday));
        Assert.False(SuggestionPlanner.IsSuggestionDay(thursday, mornings, Settings));
        Assert.Equal(MorningStep.Wait, Next(At(thursday, 7, 0), mornings));
        var friday = Monday.AddDays(4);
        Assert.True(SuggestionPlanner.IsSuggestionDay(friday, mornings, Settings));

        // Ignored on Friday too: next is four days later.
        mornings.Add(Ignored(friday));
        Assert.False(SuggestionPlanner.IsSuggestionDay(friday.AddDays(3), mornings, Settings));
        Assert.True(SuggestionPlanner.IsSuggestionDay(friday.AddDays(4), mornings, Settings));

        // Any reaction resets it.
        mornings.Add(new MorningRecord(friday.AddDays(4), At(friday.AddDays(4), 6, 0), Engaged: true, Researched: true));
        Assert.True(SuggestionPlanner.IsSuggestionDay(friday.AddDays(5), mornings, Settings));
    }

    [Fact]
    public void Mornings_without_a_card_neither_count_nor_break_the_run()
    {
        List<MorningRecord> mornings =
        [
            Ignored(Monday),
            new(Monday.AddDays(1), Researched: true),
            Ignored(Monday.AddDays(2)),
            new(Monday.AddDays(3), Researched: true),
        ];
        Assert.Equal(2, SuggestionPlanner.IgnoredInARow(mornings, Monday.AddDays(4)));
        mornings.Insert(0, new MorningRecord(Monday.AddDays(-1), At(Monday.AddDays(-1), 6, 0), Engaged: true, Researched: true));
        Assert.Equal(2, SuggestionPlanner.IgnoredInARow(mornings, Monday.AddDays(4)));
    }

    [Theory]
    [InlineData(0, 3, 1)]
    [InlineData(2, 3, 1)]
    [InlineData(3, 3, 2)]
    [InlineData(4, 3, 4)]
    [InlineData(5, 3, 7)]
    [InlineData(40, 3, 7)]
    [InlineData(10, 0, 1)]
    public void Back_off_gap(int ignored, int threshold, int days) =>
        Assert.Equal(days, SuggestionPlanner.BackOffDays(ignored, threshold));

    [Fact]
    public void Settings_are_clamped()
    {
        var odd = new SuggestionSettings(ShowAfterHour: 30, LastHour: 2, LookbackHours: 1, MaxIdeas: 50, NoRepeatDays: 0, IgnoredMorningsBeforeBackOff: -1).Normalize();
        Assert.Equal(23, odd.ShowAfterHour);
        Assert.Equal(24, odd.LastHour);
        Assert.Equal(24, odd.LookbackHours);
        Assert.Equal(5, odd.MaxIdeas);
        Assert.Equal(3, odd.NoRepeatDays);
        Assert.Equal(0, odd.IgnoredMorningsBeforeBackOff);
    }

    private static MorningRecord Ignored(DateOnly day) => new(day, At(day, 6, 10), Engaged: false, Researched: true);

    private static MorningStep Next(DateTimeOffset now, IReadOnlyList<MorningRecord> mornings, bool hasIdeas = true, SuggestionPresence? presence = null) =>
        SuggestionPlanner.Next(now, Settings, mornings, hasIdeas, () => presence ?? AtDesk);

    private static DateTimeOffset At(DateOnly day, int hour, int minute) => new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(5.5));
}
