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

    public static ClickySettings Default { get; } = new();

    /// <summary>Repairs nulls a hand-edited file can introduce (e.g. "general": null).</summary>
    public ClickySettings Normalize() => this with
    {
        SchemaVersion = SchemaVersion <= 0 ? CurrentSchemaVersion : SchemaVersion,
        General = General ?? new(),
        Logging = Logging ?? new(),
    };
}

public sealed record GeneralSettings
{
    public bool StartWithWindows { get; init; }
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
