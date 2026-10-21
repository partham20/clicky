namespace Clicky.Core.Settings;

/// <summary>Proactive suggestions (M17), in settings.json under "suggestions".</summary>
public sealed record SuggestionSettings(
    bool Enabled = true,
    int ShowAfterHour = SuggestionSettings.DefaultShowAfterHour,
    int LastHour = SuggestionSettings.DefaultLastHour,
    int LookbackHours = SuggestionSettings.DefaultLookbackHours,
    int MaxIdeas = SuggestionSettings.DefaultMaxIdeas,
    bool SearchWeb = true,
    int NoRepeatDays = SuggestionSettings.DefaultNoRepeatDays,
    int IgnoredMorningsBeforeBackOff = SuggestionSettings.DefaultIgnoredMorningsBeforeBackOff)
{
    public const int DefaultShowAfterHour = 6;
    public const int DefaultLastHour = 12;
    public const int DefaultLookbackHours = 72;
    public const int DefaultMaxIdeas = 3;
    public const int DefaultNoRepeatDays = 14;
    public const int DefaultIgnoredMorningsBeforeBackOff = 3;

    /// <summary>Research every morning and show a card with a few ideas.</summary>
    public bool Enabled { get; init; } = Enabled;

    /// <summary>The morning card appears from this local hour (0-23), once you are at the PC.</summary>
    public int ShowAfterHour { get; init; } = ShowAfterHour;

    /// <summary>Not shown on its own at or after this local hour; the ideas stay in the tray menu for the rest of the day.</summary>
    public int LastHour { get; init; } = LastHour;

    /// <summary>How far back the research looks in connected apps (24-168 h).</summary>
    public int LookbackHours { get; init; } = LookbackHours;

    /// <summary>At most this many ideas a morning (1-5).</summary>
    public int MaxIdeas { get; init; } = MaxIdeas;

    /// <summary>Also search the web (news, releases, docs) for things that matter to your work.</summary>
    public bool SearchWeb { get; init; } = SearchWeb;

    /// <summary>An idea like one shown in the last this-many days is dropped (3-90).</summary>
    public int NoRepeatDays { get; init; } = NoRepeatDays;

    /// <summary>After this many mornings in a row with no reaction, the card comes less often (0 never backs off).</summary>
    public int IgnoredMorningsBeforeBackOff { get; init; } = IgnoredMorningsBeforeBackOff;

    public SuggestionSettings Normalize()
    {
        var show = Math.Clamp(ShowAfterHour, 0, 23);
        return this with
        {
            ShowAfterHour = show,
            LastHour = Math.Clamp(LastHour, show + 1, 24),
            LookbackHours = Math.Clamp(LookbackHours, 24, 168),
            MaxIdeas = Math.Clamp(MaxIdeas, 1, 5),
            NoRepeatDays = Math.Clamp(NoRepeatDays, 3, 90),
            IgnoredMorningsBeforeBackOff = Math.Clamp(IgnoredMorningsBeforeBackOff, 0, 30),
        };
    }
}
