using System.Globalization;
using Clicky.Core.Assistant;
using Clicky.Core.Memory;

namespace Clicky.Core.Routines;

/// <summary>
/// The runner until persistent Clickys (M14) exist: asks Claude in a fresh conversation of its own
/// (never the Talk conversation), with memory, and keeps the reply in the routine's history.
/// Nothing is spoken or recorded to memory.
/// </summary>
public sealed class AssistantClickyRunner(ClaudeAssistant assistant, IMemoryService memory, TimeProvider? time = null) : IClickyRunner
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<IReadOnlyList<ClickyRef>> ListClickysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ClickyRef>>([]);

    public async Task<RoutineRunResult> RunRoutineAsync(RoutineRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var conversation = new Conversation(_time, await memory.GetPromptAsync(cancellationToken).ConfigureAwait(false));
        var claudeRequest = new ClaudeRequest(Compose(request, TimeZoneInfo.Local), RequestSource.Typed);

        ReplyCompleted? completed = null;
        await foreach (var replyEvent in assistant.ReplyAsync(conversation, claudeRequest, cancellationToken).ConfigureAwait(false))
        {
            if (replyEvent is ReplyCompleted done)
            {
                completed = done;
            }
        }

        if (completed is null)
        {
            return RoutineRunResult.Failure("Claude didn't answer.");
        }

        if (completed.Stop == ReplyStop.Refused)
        {
            return RoutineRunResult.Failure("Claude declined to do this.");
        }

        var text = completed.SpokenText.Trim();
        return text.Length == 0
            ? RoutineRunResult.Failure("Claude's answer was empty.")
            : RoutineRunResult.Success(text, text);
    }

    /// <summary>
    /// The routine's prompt with a line of context first: nobody is at the screen waiting, so answer
    /// in text and don't point at anything; and whether the run is late.
    /// </summary>
    internal static string Compose(RoutineRunRequest request, TimeZoneInfo zone)
    {
        var scheduled = TimeZoneInfo.ConvertTime(request.ScheduledFor, zone);
        var when = scheduled.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        var late = request.IsCatchUp ? " It is running late because the PC was asleep, off or offline at that time." : string.Empty;
        return $"[Routine \"{request.RoutineName}\", scheduled for {when}.{late} " +
               "Nobody is waiting at the screen: reply in text that reads well later, and don't point at or draw on the screen.]\n" +
               request.Prompt;
    }
}
