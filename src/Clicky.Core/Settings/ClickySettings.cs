using Clicky.Core.Capture;

namespace Clicky.Core.Settings;

/// <summary>
/// Root of settings.json. Immutable records so change detection is plain value equality and
/// readers never observe a half-applied update. Add new sections as nested records with defaults;
/// missing properties in an older file deserialize to those defaults.
/// </summary>
/// <remarks>
/// The source-generated reader assigns every init-only property, so one missing from the file
/// gets 0/false/null rather than its initializer. Sections therefore come back null and are
/// repaired by <see cref="Normalize"/>, and leaf records with non-zero defaults are positional:
/// a missing property takes the constructor parameter's default instead.
/// </remarks>
public sealed record ClickySettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public GeneralSettings General { get; init; } = new();

    public LoggingSettings Logging { get; init; } = new();

    public OverlaySettings Overlay { get; init; } = new();

    public HotkeySettings Hotkeys { get; init; } = new();

    public CaptureSettings Capture { get; init; } = new();

    public VoiceSettings Voice { get; init; } = new();

    public SpeechSettings Speech { get; init; } = new();

    public ClaudeSettings Claude { get; init; } = new();

    public MemorySettings Memory { get; init; } = new();

    public ConnectorSettings Connectors { get; init; } = new();

    public DictationSettings Dictation { get; init; } = new();

    public TextModeSettings Text { get; init; } = new();

    public InkSettings Ink { get; init; } = new();

    public DebugSettings Debug { get; init; } = new();

    public static ClickySettings Default { get; } = new();

    /// <summary>Repairs nulls and out-of-range values a hand-edited file can introduce (e.g. "general": null).</summary>
    public ClickySettings Normalize() => this with
    {
        SchemaVersion = SchemaVersion <= 0 ? CurrentSchemaVersion : SchemaVersion,
        General = General ?? new(),
        Logging = Logging ?? new(),
        Overlay = Overlay ?? new(),
        Hotkeys = (Hotkeys ?? new()).Normalize(),
        Capture = (Capture ?? new()).Normalize(),
        Voice = (Voice ?? new()).Normalize(),
        Speech = (Speech ?? new()).Normalize(),
        Claude = (Claude ?? new()).Normalize(),
        Memory = (Memory ?? new()).Normalize(),
        Connectors = (Connectors ?? new()).Normalize(),
        Dictation = (Dictation ?? new()).Normalize(),
        Text = (Text ?? new()).Normalize(),
        Ink = Ink ?? new(),
        Debug = Debug ?? new(),
    };
}

public sealed record CaptureSettings(
    CaptureMode Mode = CaptureMode.MonitorUnderCursor,
    int MaxLongEdge = CaptureSettings.DefaultMaxLongEdge,
    int MaxPixels = CaptureSettings.DefaultMaxPixels,
    bool IncludeCursor = false)
{
    public const int DefaultMaxLongEdge = 1568;
    public const int DefaultMaxPixels = 1_150_000;
    public const int MinMaxLongEdge = 256;
    public const int MaxMaxLongEdge = 8192;
    public const int MinMaxPixels = 65_536;
    public const int MaxMaxPixels = 33_554_432;

    public CaptureMode Mode { get; init; } = Mode;

    /// <summary>Longest side of the screenshot sent to the model. 1568 is Claude's recommended maximum.</summary>
    public int MaxLongEdge { get; init; } = MaxLongEdge;

    /// <summary>Pixel budget (about 1.15 MP keeps an image near 1,600 tokens).</summary>
    public int MaxPixels { get; init; } = MaxPixels;

    /// <summary>Draw the mouse pointer into screenshots.</summary>
    public bool IncludeCursor { get; init; } = IncludeCursor;

    public CaptureSettings Normalize() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : CaptureMode.MonitorUnderCursor,
        MaxLongEdge = Math.Clamp(MaxLongEdge, MinMaxLongEdge, MaxMaxLongEdge),
        MaxPixels = Math.Clamp(MaxPixels, MinMaxPixels, MaxMaxPixels),
    };
}

public sealed record DebugSettings
{
    /// <summary>Also write every screenshot to %AppData%\Clicky\debug\captures, with a crosshair at the pointer.</summary>
    public bool SaveCaptures { get; init; }

    /// <summary>Also write every Talk recording to %AppData%\Clicky\debug\audio as a 16 kHz WAV.</summary>
    public bool SaveAudio { get; init; }
}

public sealed record GeneralSettings
{
    public bool StartWithWindows { get; init; }
}

public sealed record OverlaySettings(bool HideFloatingCursor = false, int FollowSmoothingMs = 40)
{
    public const int MaxFollowSmoothingMs = 300;

    /// <summary>"Hide floating cursor": the buddy stops following the pointer and docks to Home instead.</summary>
    public bool HideFloatingCursor { get; init; } = HideFloatingCursor;

    /// <summary>
    /// How loosely the buddy trails the pointer: the smoothing time constant in ms. 0 pins it to the
    /// pointer; values above <see cref="MaxFollowSmoothingMs"/> are clamped.
    /// </summary>
    public int FollowSmoothingMs { get; init; } = FollowSmoothingMs;
}

public sealed record LoggingSettings(LogLevelSetting MinimumLevel = LogLevelSetting.Information)
{
    public LogLevelSetting MinimumLevel { get; init; } = MinimumLevel;
}

/// <summary>Kept independent of Serilog/MEL so Core stays dependency-light.</summary>
public enum LogLevelSetting
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
}
