namespace Clicky.Core.Clickys;

/// <summary>
/// One Clicky: a persistent helper with its own instructions, notes, chat, files folder and run
/// history, stored in clickys.db. Ids are 32 hex characters (a Guid's "N" form).
/// </summary>
/// <param name="Instructions">Standing instructions: who it is and what it does, sent with every run.</param>
/// <param name="Notes">What it should remember between runs; the user edits these.</param>
/// <param name="Color">Accent colour in Home, "#RRGGBB".</param>
/// <param name="UsesComputer">May see the screen and control the computer (M13) in its runs.</param>
/// <param name="MaxSteps">Most agent steps one run may take before it is stopped.</param>
/// <param name="Unread">Replies the user has not looked at in Home yet.</param>
public sealed record ClickyAgent(
    string Id,
    string Name,
    string Instructions,
    string Notes,
    string Color,
    bool UsesComputer,
    int MaxSteps,
    int Unread,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastActivityAt);

/// <summary>What the user fills in to create or edit a Clicky.</summary>
public sealed record ClickyDraft(
    string Name,
    string Instructions = "",
    string Notes = "",
    string Color = ClickyDraft.DefaultColor,
    bool UsesComputer = false,
    int MaxSteps = ClickyDraft.DefaultMaxSteps)
{
    public const string DefaultColor = "#2F81F7";

    internal static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");
    public const int DefaultMaxSteps = 30;
    public const int MinMaxSteps = 1;
    public const int MaxMaxSteps = 200;
    public const int MaxNameLength = 60;
    public const int MaxInstructionsLength = 8000;
    public const int MaxNotesLength = 8000;

    /// <summary>Trims and clamps what a dialog or a caller sent.</summary>
    /// <exception cref="ArgumentException">No name.</exception>
    public ClickyDraft Normalize()
    {
        var name = (Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("A Clicky needs a name.", nameof(Name));
        }

        return this with
        {
            Name = name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name,
            Instructions = Cap((Instructions ?? string.Empty).Trim(), MaxInstructionsLength),
            Notes = Cap((Notes ?? string.Empty).Trim(), MaxNotesLength),
            Color = IsColor(Color) ? Color.ToUpperInvariant() : DefaultColor,
            MaxSteps = Math.Clamp(MaxSteps, MinMaxSteps, MaxMaxSteps),
        };
    }

    public static ClickyDraft From(ClickyAgent clicky) =>
        new(clicky.Name, clicky.Instructions, clicky.Notes, clicky.Color, clicky.UsesComputer, clicky.MaxSteps);

    internal static bool IsColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && !value.AsSpan(1).ContainsAnyExcept(HexDigits);

    private static string Cap(string value, int max) => value.Length > max ? value[..max] : value;
}

/// <summary>Who wrote a chat message. Stored in clickys.db by name; do not rename.</summary>
public enum ClickyMessageRole
{
    /// <summary>Typed by the user in Home.</summary>
    User,

    /// <summary>A routine's prompt (M15), posted on the user's behalf.</summary>
    Routine,

    /// <summary>The Clicky's reply.</summary>
    Assistant,

    /// <summary>Clicky's own note in the chat: a failure, a stop.</summary>
    System,
}

/// <param name="RunId">The run that wrote it, or that it started.</param>
public sealed record ClickyMessage(long Id, string ClickyId, string? RunId, ClickyMessageRole Role, string Text, DateTimeOffset At);

/// <summary>Stored in clickys.db by name; do not rename.</summary>
public enum ClickyRunStatus
{
    /// <summary>Waiting for this Clicky's previous run (or another computer-using run) to finish.</summary>
    Queued,

    Running,

    /// <summary>Held between steps until resumed.</summary>
    Paused,

    Completed,

    Failed,

    /// <summary>Stopped by the user.</summary>
    Cancelled,

    /// <summary>Clicky quit while it was queued or running.</summary>
    Interrupted,
}

/// <summary>Stored in clickys.db by name; do not rename.</summary>
public enum ClickyRunTrigger
{
    /// <summary>The user wrote in the Clicky's chat.</summary>
    Chat,

    /// <summary>A routine (M15).</summary>
    Routine,

    /// <summary>Started from code or another feature (a retry, a voice command).</summary>
    Manual,
}

public sealed record ClickyRun(
    string Id,
    string ClickyId,
    ClickyRunTrigger Trigger,
    string Prompt,
    ClickyRunStatus Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? EndedAt = null,
    int Steps = 0,
    string? Summary = null,
    string? Error = null,
    string? Model = null,
    long InputTokens = 0,
    long OutputTokens = 0,
    decimal? CostUsd = null,
    string? RoutineId = null,
    string? RoutineName = null,
    DateTimeOffset? ScheduledFor = null)
{
    public bool IsActive => Status is ClickyRunStatus.Queued or ClickyRunStatus.Running or ClickyRunStatus.Paused;

    public TimeSpan? Duration => StartedAt is { } start && EndedAt is { } end ? end - start : null;
}

/// <summary>Stored in clickys.db by name; do not rename.</summary>
public enum ClickyStepKind
{
    /// <summary>A request went to Claude.</summary>
    Request,

    /// <summary>What the Clicky said in a step.</summary>
    Text,

    /// <summary>Something it showed or did (point, click, type…).</summary>
    Action,

    /// <summary>An action that failed validation or was refused.</summary>
    Rejected,

    /// <summary>A screenshot was taken for a step.</summary>
    Screenshot,

    /// <summary>Paused, resumed, stopped, step cap reached.</summary>
    Control,

    Error,
}

/// <summary>One line of a run's activity log, shown in Home.</summary>
public sealed record ClickyRunStep(string RunId, int Seq, DateTimeOffset At, ClickyStepKind Kind, string Text);

/// <summary>Asks a Clicky to do something.</summary>
/// <param name="Prompt">The task, as the user or the routine wrote it.</param>
/// <param name="ScheduledFor">When the routine was due (M15), for the prompt and history.</param>
/// <param name="IsCatchUp">The routine was missed (PC asleep) and runs late.</param>
public sealed record ClickyRunRequest(
    string Prompt,
    ClickyRunTrigger Trigger = ClickyRunTrigger.Routine,
    string? RoutineId = null,
    string? RoutineName = null,
    DateTimeOffset? ScheduledFor = null,
    bool IsCatchUp = false);

/// <param name="Summary">One line for a routine's history or a notification.</param>
/// <param name="Reply">The whole reply as posted in the chat.</param>
public sealed record ClickyRunOutcome(string RunId, ClickyRunStatus Status, bool Succeeded, string Summary, string? Reply);

/// <summary>What changed, so Home can refresh just that part.</summary>
public enum ClickyChange
{
    ClickyAdded,
    ClickyUpdated,
    ClickyDeleted,
    MessageAdded,
    RunChanged,
    StepAdded,
}

public sealed class ClickyChangedEventArgs(ClickyChange change, string clickyId, string? runId = null) : EventArgs
{
    public ClickyChange Change { get; } = change;

    public string ClickyId { get; } = clickyId;

    public string? RunId { get; } = runId;
}

/// <summary>A run finished (any end state), for notifications.</summary>
public sealed class ClickyRunFinishedEventArgs(ClickyAgent clicky, ClickyRun run, ClickyRunOutcome outcome) : EventArgs
{
    public ClickyAgent Clicky { get; } = clicky;

    public ClickyRun Run { get; } = run;

    public ClickyRunOutcome Outcome { get; } = outcome;
}

/// <summary>The Clicky or run does not exist (deleted, or a stale id from a routine).</summary>
public sealed class ClickyNotFoundException : Exception
{
    public ClickyNotFoundException()
    {
    }

    public ClickyNotFoundException(string message)
        : base(message)
    {
    }

    public ClickyNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
