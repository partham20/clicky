using Clicky.Core.Clickys;

namespace Clicky.Core.Routines;

/// <summary>
/// Runs routines in M14's Clickys: the prompt goes into the Clicky's chat as a routine turn and the
/// reply lands there as unread, never spoken. A routine with no Clicky (made by Talk, say) goes to
/// <paramref name="unassigned"/>, which keeps the answer in the routine's history.
/// </summary>
public sealed class ClickyServiceRunner(ClickyService clickys, IClickyRunner unassigned) : IClickyRunner
{
    public async Task<IReadOnlyList<ClickyRef>> ListClickysAsync(CancellationToken cancellationToken = default) =>
        [.. (await clickys.ListAsync(cancellationToken).ConfigureAwait(false)).Select(c => new ClickyRef(c.Id, c.Name))];

    public async Task<RoutineRunResult> RunRoutineAsync(RoutineRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ClickyId))
        {
            return await unassigned.RunRoutineAsync(request, cancellationToken).ConfigureAwait(false);
        }

        ClickyRunOutcome outcome;
        try
        {
            outcome = await clickys.RunAsync(
                request.ClickyId,
                new ClickyRunRequest(request.Prompt, ClickyRunTrigger.Routine, request.RoutineId, request.RoutineName, request.ScheduledFor, request.IsCatchUp),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ClickyNotFoundException)
        {
            return RoutineRunResult.Failure("The Clicky this routine belongs to no longer exists. Edit the routine to pick another.");
        }

        // A cancelled run comes back as an outcome, not an exception. When the engine cancelled it
        // (sleep, Stop, quitting) it decides what that means; when the user stopped it in Home, or
        // Clicky closed under it, it is recorded as stopped rather than failed.
        cancellationToken.ThrowIfCancellationRequested();
        return outcome.Status is ClickyRunStatus.Cancelled or ClickyRunStatus.Interrupted
            ? RoutineRunResult.Stopped(outcome.Summary)
            : new RoutineRunResult(outcome.Succeeded, outcome.Summary);
    }
}
