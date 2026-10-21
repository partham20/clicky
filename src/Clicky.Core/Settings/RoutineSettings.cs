namespace Clicky.Core.Settings;

/// <summary>settings.routines: how scheduled routines behave. The routines themselves live in routines.db.</summary>
/// <remarks>Positional so missing properties read as these defaults (see <see cref="CaptureSettings"/>).</remarks>
public sealed record RoutineSettings(
    bool Enabled = true,
    int CatchUpHours = RoutineSettings.DefaultCatchUpHours,
    bool HoldDuringCalls = true,
    int MaxConcurrentRuns = 1,
    int RunTimeoutMinutes = RoutineSettings.DefaultRunTimeoutMinutes,
    bool NotifyWhenPaused = true)
{
    public const int DefaultCatchUpHours = 12;
    public const int MaxCatchUpHours = 7 * 24;
    public const int MaxMaxConcurrentRuns = 3;
    public const int DefaultRunTimeoutMinutes = 30;
    public const int MaxRunTimeoutMinutes = 240;

    /// <summary>Off: no routine runs on its own (Run now still works).</summary>
    public bool Enabled { get; init; } = Enabled;

    /// <summary>
    /// A run missed while the PC was asleep, off or Clicky was closed is caught up once if it was due
    /// within this many hours; older ones are skipped. 0 never catches up.
    /// </summary>
    public int CatchUpHours { get; init; } = CatchUpHours;

    /// <summary>Wait while the user is in a call, sharing the screen or presenting, then run.</summary>
    public bool HoldDuringCalls { get; init; } = HoldDuringCalls;

    /// <summary>Routines running at the same time; the rest queue.</summary>
    public int MaxConcurrentRuns { get; init; } = MaxConcurrentRuns;

    /// <summary>A run still going after this long is stopped and counts as a failure.</summary>
    public int RunTimeoutMinutes { get; init; } = RunTimeoutMinutes;

    /// <summary>Show a Windows notification when Clicky pauses a routine after repeated failures.</summary>
    public bool NotifyWhenPaused { get; init; } = NotifyWhenPaused;

    public TimeSpan CatchUpWindow => TimeSpan.FromHours(CatchUpHours);

    public TimeSpan RunTimeout => TimeSpan.FromMinutes(RunTimeoutMinutes);

    public RoutineSettings Normalize() => this with
    {
        CatchUpHours = Math.Clamp(CatchUpHours, 0, MaxCatchUpHours),
        MaxConcurrentRuns = Math.Clamp(MaxConcurrentRuns, 1, MaxMaxConcurrentRuns),
        RunTimeoutMinutes = Math.Clamp(RunTimeoutMinutes, 1, MaxRunTimeoutMinutes),
    };
}
