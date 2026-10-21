using Clicky.Core.Settings;
using Clicky.Core.Voice;

namespace Clicky.Core.Suggestions;

/// <summary>What the morning loop should do now.</summary>
public enum MorningStep
{
    Wait,

    /// <summary>Run today's research and editor passes.</summary>
    Research,

    /// <summary>Show the morning card.</summary>
    Show,
}

/// <summary>The user and the PC right now, as far as the morning card cares.</summary>
/// <param name="Idle">Time since the last keyboard or mouse input.</param>
/// <param name="Quiet">What <see cref="IQuietDetector"/> sees: calls, screen sharing, Focus Assist, full screen.</param>
/// <param name="Busy">Clicky is listening, thinking or speaking (a Talk or an agent is running).</param>
public sealed record SuggestionPresence(TimeSpan Idle, QuietReasons Quiet, bool Busy);

/// <summary>
/// When to research and when to show the morning card. Pure rules over the clock, the settings and
/// the record of past mornings, so the schedule and the back-off can be tested without waiting
/// for mornings.
/// </summary>
/// <remarks>
/// Research starts an hour before settings.suggestions.showAfterHour so the card is ready when the
/// user sits down. The card appears at or after that hour, before lastHour, once the user is at
/// the PC and not in a call, sharing the screen, in Focus Assist or full screen, and not in the
/// middle of talking to Clicky. After ignoredMorningsBeforeBackOff shown mornings in a row with
/// no reaction, the card skips days: every 2nd day, then every 4th, then weekly, until the user
/// reacts again.
/// </remarks>
public static class SuggestionPlanner
{
    public static readonly TimeSpan ResearchLead = TimeSpan.FromHours(1);

    /// <summary>Input this recent means the user is at the PC.</summary>
    public static readonly TimeSpan ActiveWithin = TimeSpan.FromMinutes(2);

    /// <summary>The longest gap back-off goes to.</summary>
    public const int MaxBackOffDays = 7;

    /// <summary>Conditions that hold the card back. Muted speakers and Quiet mode don't: the card is silent anyway.</summary>
    public const QuietReasons HoldsCard = QuietReasons.InCall | QuietReasons.ScreenSharing | QuietReasons.FocusAssist | QuietReasons.FullScreen;

    public static MorningStep Next(
        DateTimeOffset localNow,
        SuggestionSettings settings,
        IReadOnlyList<MorningRecord> mornings,
        bool hasIdeasToShow,
        Func<SuggestionPresence> presence)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mornings);
        ArgumentNullException.ThrowIfNull(presence);
        if (!settings.Enabled)
        {
            return MorningStep.Wait;
        }

        var today = DateOnly.FromDateTime(localNow.DateTime);
        var showFrom = localNow.Date.AddHours(settings.ShowAfterHour);
        var until = localNow.Date.AddHours(settings.LastHour);
        var clock = localNow.DateTime;
        if (clock < showFrom - ResearchLead || clock >= until || !IsSuggestionDay(today, mornings, settings))
        {
            return MorningStep.Wait;
        }

        var morning = mornings.LastOrDefault(m => m.Day == today);
        if (morning is not { Researched: true })
        {
            return MorningStep.Research;
        }

        if (morning.ShownAt is not null || !hasIdeasToShow || clock < showFrom)
        {
            return MorningStep.Wait;
        }

        // Looked at last: checking for calls and screen sharing costs a few milliseconds.
        var now = presence();
        return now.Idle <= ActiveWithin && !now.Busy && (now.Quiet & HoldsCard) == 0
            ? MorningStep.Show
            : MorningStep.Wait;
    }

    /// <summary>False on a day back-off skips. Mornings without a card neither count nor break the run.</summary>
    public static bool IsSuggestionDay(DateOnly today, IReadOnlyList<MorningRecord> mornings, SuggestionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(mornings);
        ArgumentNullException.ThrowIfNull(settings);
        var gap = BackOffDays(IgnoredInARow(mornings, today), settings.IgnoredMorningsBeforeBackOff);
        if (gap <= 1)
        {
            return true;
        }

        var lastShown = mornings.Where(m => m.ShownAt is not null && m.Day < today).Select(m => m.Day).DefaultIfEmpty().Max();
        return lastShown == default || today.DayNumber - lastShown.DayNumber >= gap;
    }

    /// <summary>Shown mornings before <paramref name="before"/> with no reaction, counting back to the last one with a reaction.</summary>
    public static int IgnoredInARow(IReadOnlyList<MorningRecord> mornings, DateOnly before)
    {
        ArgumentNullException.ThrowIfNull(mornings);
        var count = 0;
        foreach (var morning in mornings.Where(m => m.Day < before && m.ShownAt is not null).OrderByDescending(m => m.Day))
        {
            if (morning.Engaged)
            {
                break;
            }

            count++;
        }

        return count;
    }

    /// <summary>1 (every day) below the threshold; then 2, 4, then <see cref="MaxBackOffDays"/>.</summary>
    public static int BackOffDays(int ignoredInARow, int threshold)
    {
        if (threshold <= 0 || ignoredInARow < threshold)
        {
            return 1;
        }

        var steps = Math.Min(ignoredInARow - threshold + 1, 3);
        return Math.Min(1 << steps, MaxBackOffDays);
    }
}
