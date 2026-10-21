namespace Clicky.Core.Settings;

/// <summary>settings.memory: PROFILE.md / VOLATILE.md in %AppData%\Clicky\memory.</summary>
/// <remarks>Positional so missing properties read as these defaults (see <see cref="CaptureSettings"/>).</remarks>
public sealed record MemorySettings(bool Enabled = true, bool AutoUpdate = true, int IdleMinutes = MemorySettings.DefaultIdleMinutes)
{
    public const int DefaultIdleMinutes = 3;
    public const int MaxIdleMinutes = 60;

    /// <summary>Send memory with every request. Off: Clicky neither reads nor updates it (the files stay).</summary>
    public bool Enabled { get; init; } = Enabled;

    /// <summary>Let Clicky update memory after conversations. Off: memory is read but only changed by hand.</summary>
    public bool AutoUpdate { get; init; } = AutoUpdate;

    /// <summary>A conversation counts as over after this many quiet minutes; then the updater pass runs.</summary>
    public int IdleMinutes { get; init; } = IdleMinutes;

    public MemorySettings Normalize() => this with { IdleMinutes = Math.Clamp(IdleMinutes, 1, MaxIdleMinutes) };
}
