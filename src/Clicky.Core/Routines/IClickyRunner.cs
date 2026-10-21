namespace Clicky.Core.Routines;

/// <summary>
/// Runs a routine's prompt in a Clicky. M14 implements it over its Clickys: the prompt goes into
/// that Clicky's chat as a routine turn, runs with its memory and files, and the result lands in the
/// chat with an unread badge, never spoken. Until then <see cref="AssistantClickyRunner"/> asks
/// Claude directly and the result is kept in the routine's history.
/// </summary>
public interface IClickyRunner
{
    /// <summary>Clickys a routine can belong to, for the editor's picker. Empty when there are none yet.</summary>
    Task<IReadOnlyList<ClickyRef>> ListClickysAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs one routine and returns when the Clicky has finished.</summary>
    /// <returns>Whether it worked, with a one-line summary for the list.</returns>
    /// <exception cref="Assistant.ClaudeBackendException">
    /// Claude failed. <see cref="Assistant.ClaudeFailure.Network"/> while offline makes the routine
    /// wait for the network instead of counting as a failure.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancelled (sleep, Clicky quitting, the user, or the run timeout).</exception>
    Task<RoutineRunResult> RunRoutineAsync(RoutineRunRequest request, CancellationToken cancellationToken = default);
}

public sealed record ClickyRef(string Id, string Name);

/// <param name="ClickyId">Null for the main Clicky.</param>
/// <param name="ScheduledFor">The run this is (the latest missed one for a catch-up).</param>
/// <param name="IsCatchUp">Running late after sleep, shutdown or an offline spell; the prompt may want to say so.</param>
public sealed record RoutineRunRequest(
    string RoutineId,
    string RoutineName,
    string Prompt,
    string? ClickyId,
    DateTimeOffset ScheduledFor,
    bool IsCatchUp,
    RoutineTrigger Trigger);

/// <param name="Summary">One line for the routine list, e.g. the first line of the reply or what went wrong.</param>
/// <param name="Output">The full result, if the runner has no chat to put it in (kept in the run history).</param>
public sealed record RoutineRunResult(bool Succeeded, string? Summary = null, string? Output = null)
{
    public static RoutineRunResult Success(string? summary = null, string? output = null) => new(true, summary, output);

    public static RoutineRunResult Failure(string summary) => new(false, summary);
}
