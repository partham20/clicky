using System.Globalization;
using Clicky.Core.Actions;
using Clicky.Core.Agent;
using Clicky.Core.Assistant;
using Clicky.Core.Geometry;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Routines;

/// <summary>Turns a routine Claude proposed in Talk or text mode into a saved routine, once the user agrees.</summary>
public interface IRoutineProposer
{
    /// <summary>
    /// Shows the proposal and returns at once; the card stays up until the user answers, also while
    /// they ask other things. Proposals are shown one at a time, in order.
    /// </summary>
    /// <param name="conversation">Where the answer is reported to Claude, if it still can be.</param>
    /// <param name="toolCallId">The tool call that carried it (API backend); null for an action tag.</param>
    /// <param name="near">Where the user was asking (physical pixels), so the card shows on that monitor.</param>
    void Propose(RoutineAction action, Conversation conversation, string? toolCallId, PixelRect? near);
}

/// <summary>
/// Asks on the agent card, the same permission card computer use asks on (Save routine / Not now,
/// never Always), then creates the routine with <see cref="RoutineEngine"/>. Routines made this way
/// belong to no Clicky: their results go to the routine's history and a notification.
/// </summary>
/// <remarks>
/// The answer goes back to Claude as the tool call's result when the conversation hasn't moved on
/// yet; otherwise Claude was already told it is waiting on the user (<see cref="DefaultToolResults.Proposed"/>).
/// Action tags have no result to send.
/// </remarks>
public sealed class RoutineProposer : IRoutineProposer, IAsyncDisposable
{
    public const string CardTask = RoutineConfirmer.CreateTask;
    public const string SaveLabel = RoutineConfirmer.SaveLabel;

    private readonly RoutineEngine _engine;
    private readonly RoutineConfirmer _confirmer;
    private readonly ILogger<RoutineProposer> _logger;
    private readonly TimeProvider _time;
    private readonly Func<TimeZoneInfo> _zone;
    private readonly CancellationTokenSource _disposed = new();
    private readonly Lock _gate = new();
    private Task _queue = Task.CompletedTask;

    public RoutineProposer(RoutineEngine engine, IAgentPresenter presenter, ILogger<RoutineProposer> logger, TimeProvider? time = null, Func<TimeZoneInfo>? zone = null)
    {
        _engine = engine;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _zone = zone ?? (() => TimeZoneInfo.Local);
        _confirmer = new RoutineConfirmer(presenter, _time, _zone);
    }

    /// <summary>Raised after each proposal is answered (or fails), with what Claude is told.</summary>
    public event EventHandler<ToolOutcome>? Answered;

    public void Propose(RoutineAction action, Conversation conversation, string? toolCallId, PixelRect? near)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(conversation);
        lock (_gate)
        {
            _queue = _queue.ContinueWith(
                _ => ProposeLoggedAsync(action, conversation, toolCallId, near),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposed.CancelAsync().ConfigureAwait(false);
        await WhenIdleAsync().ConfigureAwait(false);
        _disposed.Dispose();
    }

    /// <summary>Completes when every proposal so far has been answered.</summary>
    internal Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _queue;
        }
    }

    private async Task ProposeLoggedAsync(RoutineAction action, Conversation conversation, string? toolCallId, PixelRect? near)
    {
        ToolOutcome outcome;
        try
        {
            outcome = await ProposeAsync(action, toolCallId, near).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Clicky is closing; the default result already says the user hadn't answered.
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Routines: could not propose \"{Name}\"", action.Title);
            outcome = new ToolOutcome(toolCallId, "routine", $"Not saved: {ex.Message}", IsError: true);
        }

        Report(conversation, outcome);
        Answered?.Invoke(this, outcome);
    }

    private async Task<ToolOutcome> ProposeAsync(RoutineAction action, string? toolCallId, PixelRect? near)
    {
        var token = _disposed.Token;
        var now = _time.GetUtcNow();
        var zone = _zone();

        RoutineSchedule schedule;
        try
        {
            schedule = RoutineTools.ParseSchedule(action.Schedule);
        }
        catch (ArgumentException ex)
        {
            return Result(toolCallId, $"Not saved: {ex.Message}", isError: true);
        }

        var next = schedule.NextAfter(now, zone, now);
        if (schedule.Kind == RoutineScheduleKind.Once && next is null)
        {
            return Result(toolCallId, "Not saved: that time has already passed.", isError: true);
        }

        var draft = new RoutineDraft(action.Title.Trim(), action.Prompt.Trim(), schedule, ClickyId: null);
        using var card = await _confirmer.ConfirmCreateAsync(draft, near, token).ConfigureAwait(false);
        if (!card.Approved)
        {
            _logger.LogInformation("Routines: the user didn't save \"{Name}\"", draft.Name);
            return Result(toolCallId, $"The user chose not to save the routine \u201C{draft.Name}\u201D. Nothing was set up.", isError: false);
        }

        Routine routine;
        try
        {
            routine = await _engine.CreateAsync(draft, token).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            card.Finish($"Couldn't save it: {ex.Message}", success: false);
            return Result(toolCallId, $"Not saved: {ex.Message}", isError: true);
        }

        card.Finish(_confirmer.SavedMessage(routine), success: true);
        _logger.LogInformation("Routines: the user saved \"{Name}\" ({Schedule}) from a conversation", routine.Name, routine.Schedule.Describe(CultureInfo.InvariantCulture));

        var invariantNext = routine.NextDueAt is { } d ? $" Next run: {RoutineFormat.When(d, _time.GetUtcNow(), zone, CultureInfo.InvariantCulture)}." : string.Empty;
        return Result(
            toolCallId,
            $"The user saved the routine “{routine.Name}”: {routine.Schedule.Describe(CultureInfo.InvariantCulture)}.{invariantNext} It runs while Clicky is open; results arrive as a notification and in its history.",
            isError: false);
    }

    private static ToolOutcome Result(string? toolCallId, string text, bool isError) => new(toolCallId, "routine", text, isError);

    /// <summary>Hands the answer to the next request, if the call is still the one it has to answer.</summary>
    private static void Report(Conversation conversation, ToolOutcome outcome)
    {
        if (outcome.ToolCallId is not { } id || !conversation.PendingToolCalls.Any(c => c.Id == id))
        {
            return;
        }

        conversation.KeepUnsentResults([.. (conversation.UnsentResults ?? []).Where(r => r.ToolCallId != id), outcome]);
    }
}
