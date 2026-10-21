namespace Clicky.Core.Settings;

/// <summary>
/// Root of settings.json. Immutable records so change detection is plain value equality and
/// readers never observe a half-applied update. Add new sections as nested records with defaults;
/// missing properties in an older file deserialize to those defaults.
/// </summary>
public sealed record ClickySettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public GeneralSettings General { get; init; } = new();

    public LoggingSettings Logging { get; init; } = new();

    public OverlaySettings Overlay { get; init; } = new();

    public static ClickySettings Default { get; } = new();

    /// <summary>Repairs nulls a hand-edited file can introduce (e.g. "general": null).</summary>
    public ClickySettings Normalize() => this with
    {
        SchemaVersion = SchemaVersion <= 0 ? CurrentSchemaVersion : SchemaVersion,
        General = General ?? new(),
        Logging = Logging ?? new(),
        Overlay = Overlay ?? new(),
    };
}

public sealed record GeneralSettings
{
    public bool StartWithWindows { get; init; }
}

public sealed record OverlaySettings
{
    public const int MaxFollowSmoothingMs = 300;

    /// <summary>"Hide floating cursor": the buddy stops following the pointer and docks to Home instead.</summary>
    public bool HideFloatingCursor { get; init; }

    /// <summary>
    /// How loosely the buddy trails the pointer: the smoothing time constant in ms. 0 pins it to the
    /// pointer; values above <see cref="MaxFollowSmoothingMs"/> are clamped.
    /// </summary>
    public int FollowSmoothingMs { get; init; } = 40;
}

public sealed record LoggingSettings
{
    public LogLevelSetting MinimumLevel { get; init; } = LogLevelSetting.Information;
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
