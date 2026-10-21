using Clicky.Core.Dictation;

namespace Clicky.Core.Settings;

/// <summary>settings.dictation: speaking text into other apps (hold Ctrl+Win, or double-tap it for hands-free).</summary>
/// <remarks>
/// Positional so missing properties read as these defaults (see <see cref="CaptureSettings"/>).
/// Language, engine and microphone come from settings.voice. The personal dictionary lives in its
/// own file (dictionary.json), because Clicky writes to it whenever it learns a word.
/// </remarks>
public sealed record DictationSettings(
    int MaxMinutes = DictationSettings.DefaultMaxMinutes,
    bool TypeWhileSpeaking = true,
    bool SpokenCommands = true,
    bool LearnCorrections = true,
    int KeepHistoryDays = DictationSettings.DefaultKeepHistoryDays,
    IReadOnlyList<string>? TerminalApps = null,
    IReadOnlyList<string>? PasteApps = null,
    DictationCleanup Cleanup = DictationCleanup.Auto)
{
    public const int DefaultMaxMinutes = 30;
    public const int MaxMaxMinutes = 120;
    public const int DefaultKeepHistoryDays = 30;
    public const int MaxKeepHistoryDays = 3650;

    /// <summary>A dictation stops by itself after this long; everything said so far is kept.</summary>
    public int MaxMinutes { get; init; } = MaxMinutes;

    /// <summary>Hands-free: type each sentence as soon as it is recognised instead of all at the end.</summary>
    public bool TypeWhileSpeaking { get; init; } = TypeWhileSpeaking;

    /// <summary>"new line" and "new paragraph" become line breaks.</summary>
    public bool SpokenCommands { get; init; } = SpokenCommands;

    /// <summary>Words corrected by hand within about 20 s of being typed go into the personal dictionary.</summary>
    public bool LearnCorrections { get; init; } = LearnCorrections;

    /// <summary>
    /// Transcripts are kept as text in %AppData%\Clicky\dictation for this many days, so nothing said
    /// is ever lost. 0 keeps none.
    /// </summary>
    public int KeepHistoryDays { get; init; } = KeepHistoryDays;

    /// <summary>
    /// Extra terminal apps by exe name (e.g. "alacritty.exe"), on top of the built-in list. Line breaks
    /// are turned into spaces there so dictation can never run a command.
    /// </summary>
    public IReadOnlyList<string> TerminalApps { get; init; } = TerminalApps ?? [];

    /// <summary>Apps that drop typed Unicode input (some remote desktops and games): text is pasted there instead.</summary>
    public IReadOnlyList<string> PasteApps { get; init; } = PasteApps ?? [];

    /// <summary>When the fast model tidies punctuation and grammar (never the words).</summary>
    public DictationCleanup Cleanup { get; init; } = Cleanup;

    public TimeSpan MaxDuration => TimeSpan.FromMinutes(MaxMinutes);

    public DictationSettings Normalize() => this with
    {
        MaxMinutes = Math.Clamp(MaxMinutes, 1, MaxMaxMinutes),
        KeepHistoryDays = Math.Clamp(KeepHistoryDays, 0, MaxKeepHistoryDays),
        TerminalApps = Clean(TerminalApps),
        PasteApps = Clean(PasteApps),
        Cleanup = Enum.IsDefined(Cleanup) ? Cleanup : DictationCleanup.Auto,
    };

    // Records compare collections by reference; settings change detection needs content equality.
    public bool Equals(DictationSettings? other) =>
        other is not null
        && MaxMinutes == other.MaxMinutes
        && TypeWhileSpeaking == other.TypeWhileSpeaking
        && SpokenCommands == other.SpokenCommands
        && LearnCorrections == other.LearnCorrections
        && KeepHistoryDays == other.KeepHistoryDays
        && Cleanup == other.Cleanup
        && (TerminalApps ?? []).SequenceEqual(other.TerminalApps ?? [], StringComparer.Ordinal)
        && (PasteApps ?? []).SequenceEqual(other.PasteApps ?? [], StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(MaxMinutes, TypeWhileSpeaking, SpokenCommands, LearnCorrections, KeepHistoryDays, TerminalApps?.Count ?? 0, PasteApps?.Count ?? 0);

    private static string[] Clean(IReadOnlyList<string>? apps) =>
        [.. (apps ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())];
}
