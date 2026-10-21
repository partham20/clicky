namespace Clicky.Core.Memory;

/// <summary>Where a turn happened. Shown to the updater so it can weigh dictation differently from a request.</summary>
public enum MemoryTurnMode
{
    Voice,
    Text,
    Dictation,
    Agent,
}

/// <summary>One exchange: what the user said and what Clicky answered (spoken text, or an agent's summary).</summary>
public sealed record MemoryTurn(string User, string? Assistant, DateTimeOffset At, MemoryTurnMode Mode = MemoryTurnMode.Voice);

/// <summary>
/// The model the updater pass runs on: one plain request, text in and text out, no tools or images.
/// The backends module implements it with the active backend's fast model.
/// </summary>
public interface IMemoryModel
{
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    /// <remarks>Other exceptions are logged by the caller and the pass is skipped; the turns are kept for the next one.</remarks>
    Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken);
}

/// <summary>Raised after an updater pass changed memory, e.g. so the buddy can show "Got it, noted."</summary>
public sealed class MemoryUpdatedEventArgs(MergeResult result, bool fromFeedback) : EventArgs
{
    public MergeResult Result { get; } = result;

    /// <summary>The pass ran straight away because the user gave feedback or asked Clicky to remember something.</summary>
    public bool FromFeedback { get; } = fromFeedback;
}

/// <summary>
/// What the conversation loop, dictation and agents call. Three hooks:
/// <list type="number">
/// <item><see cref="GetPromptAsync"/> before each request: append the result to the system prompt.</item>
/// <item><see cref="RecordTurn"/> after each completed exchange.</item>
/// <item><see cref="EndConversationAsync"/> when a conversation is cleared or closed.</item>
/// </list>
/// The updater pass runs on its own after the conversation goes quiet, or right away when a turn is
/// feedback ("you talk too much") so the very next answer follows it.
/// </summary>
public interface IMemoryService
{
    event EventHandler<MemoryUpdatedEventArgs>? Updated;

    /// <summary>The memory block for a system prompt; empty when memory is off or nothing is remembered.</summary>
    /// <remarks>Reads two small files (cached); safe to call on every request. Never throws for I/O problems.</remarks>
    Task<string> GetPromptAsync(CancellationToken cancellationToken = default);

    /// <summary>Queues an exchange for the next updater pass. Non-blocking, any thread.</summary>
    void RecordTurn(MemoryTurn turn);

    /// <summary>Runs the updater pass now over the turns not yet considered.</summary>
    Task EndConversationAsync(CancellationToken cancellationToken = default);
}
