namespace Clicky.Core.Routines;

/// <summary>
/// "Do this every morning": a prompt a Clicky runs on a schedule while Clicky is open. Immutable;
/// <see cref="RoutineEngine"/> is the only writer and saves every change to routines.db.
/// </summary>
public sealed record Routine
{
    public const int MaxNameLength = 80;
    public const int MaxPromptLength = 8000;

    /// <summary>Guid "N" string.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>What the Clicky is asked each time, as the user would type it.</summary>
    public required string Prompt { get; init; }

    /// <summary>The Clicky that runs it and gets the result in its chat; null for the main Clicky.</summary>
    public string? ClickyId { get; init; }

    public required RoutineSchedule Schedule { get; init; }

    /// <summary>Off when the user paused it, or after <see cref="RoutineEngine.MaxFailuresInARow"/> failures (see <see cref="PausedReason"/>).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Why Clicky paused it by itself, shown in the list; null when the user paused it or it is running normally.</summary>
    public string? PausedReason { get; init; }

    public int ConsecutiveFailures { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Where an interval schedule counts from (creation, or the last schedule edit).</summary>
    public required DateTimeOffset AnchorAt { get; init; }

    /// <summary>The next scheduled run (UTC). In the past while a run is due but waiting; null when it never runs again.</summary>
    public DateTimeOffset? NextDueAt { get; init; }

    /// <summary>
    /// Set once a due run has been accepted (it was within the catch-up window) and is waiting for the
    /// network, the end of a call, or a free slot. Survives a restart so the run still happens once.
    /// </summary>
    public DateTimeOffset? WaitingSince { get; init; }

    public DateTimeOffset? LastRunAt { get; init; }

    public RoutineRunStatus? LastStatus { get; init; }

    /// <summary>One line about the last run: its summary or error.</summary>
    public string? LastMessage { get; init; }

    /// <summary>A Once routine that has run (or was missed): it stays in the list but never runs again.</summary>
    public bool IsFinished => Schedule.Kind == RoutineScheduleKind.Once && NextDueAt is null && WaitingSince is null;
}

/// <summary>What the user (or the assistant) fills in to make or change a routine.</summary>
public sealed record RoutineDraft(string Name, string Prompt, RoutineSchedule Schedule, string? ClickyId = null)
{
    /// <summary>What's wrong, in a sentence for the user; null if it can be saved.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return "Give the routine a name.";
        }

        if (Name.Trim().Length > Routine.MaxNameLength)
        {
            return $"Keep the name under {Routine.MaxNameLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(Prompt))
        {
            return "Say what Clicky should do each time.";
        }

        if (Prompt.Length > Routine.MaxPromptLength)
        {
            return $"Keep the instructions under {Routine.MaxPromptLength} characters.";
        }

        return Schedule?.Validate() ?? (Schedule is null ? "Pick when it runs." : null);
    }
}

/// <summary>Outcome of one run. Written to routines.db; do not rename.</summary>
public enum RoutineRunStatus
{
    Running,
    Succeeded,
    Failed,

    /// <summary>Missed while the PC was off or asleep, too long ago to catch up.</summary>
    Skipped,

    /// <summary>Stopped by sleep or lost network part way; it runs again when the PC is back.</summary>
    Interrupted,

    /// <summary>Stopped by the user, or Clicky quit.</summary>
    Cancelled,
}

/// <summary>Why a run started. Written to routines.db; do not rename.</summary>
public enum RoutineTrigger
{
    /// <summary>On time.</summary>
    Schedule,

    /// <summary>Late: the run was missed while the PC was asleep, off or offline, and is caught up once.</summary>
    CatchUp,

    /// <summary>The user pressed Run now.</summary>
    Manual,

    /// <summary>An "When Clicky starts" routine.</summary>
    Startup,
}

/// <summary>One run in a routine's history.</summary>
/// <param name="Output">The full result text, when the runner returned one (kept for the history view).</param>
public sealed record RoutineRun(
    long Id,
    string RoutineId,
    DateTimeOffset ScheduledFor,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    RoutineRunStatus Status,
    RoutineTrigger Trigger,
    string? Message,
    string? Output);
