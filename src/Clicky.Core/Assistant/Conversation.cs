namespace Clicky.Core.Assistant;

/// <summary>A tool call the model made, kept so the next request can answer it.</summary>
/// <param name="InputJson">The tool input exactly as the model sent it.</param>
/// <param name="Accepted">False when the input failed validation and the action was dropped.</param>
/// <param name="Error">Why it was dropped, for the tool result.</param>
/// <param name="ToolsetName">The Anthropic toolset the call belongs to ("computer"), echoed on its result.</param>
public sealed record ToolCallRecord(string Id, string Name, string InputJson, bool Accepted = true, string? Error = null, string? ToolsetName = null);

/// <summary>
/// A thinking block from the Messages API, passed back unchanged on later requests: the API needs
/// it to continue after tool calls, and drops (unbilled) any block the current model can't use.
/// </summary>
/// <param name="Thinking">The thinking text (empty when the API omits it).</param>
/// <param name="Signature">Signature of a thinking block; null for a redacted block.</param>
/// <param name="RedactedData">Data of a redacted thinking block.</param>
public sealed record ThinkingRecord(string Thinking, string? Signature, string? RedactedData = null);

/// <summary>One question and its answer, stored backend-neutrally.</summary>
/// <param name="UserText">What the backend was sent as the user's words, context line included.</param>
/// <param name="Screenshot">The PNG sent with the question; null once history drops it.</param>
/// <param name="AssistantText">Everything the model said.</param>
/// <param name="ToolCalls">Tool calls (API backend only), answered at the start of the next request.</param>
/// <param name="Interrupted">The user cut the reply off; the text is what was said up to then.</param>
/// <param name="Thinking">Thinking blocks that came before the text (API backend only).</param>
/// <param name="Results">
/// What the previous turn's tool calls did, sent with this turn (agent steps); calls without one
/// get <see cref="DefaultToolResults"/>.
/// </param>
public sealed record ConversationTurn(
    string UserText,
    byte[]? Screenshot,
    string AssistantText,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    DateTimeOffset At,
    bool Interrupted = false,
    IReadOnlyList<ThinkingRecord>? Thinking = null,
    IReadOnlyList<ToolOutcome>? Results = null);

/// <summary>How much history is sent back.</summary>
/// <param name="MaxTurns">Turns kept after a trim.</param>
/// <param name="TrimSlack">
/// History may grow this many turns past <paramref name="MaxTurns"/> before it is trimmed, so the
/// front of the conversation only changes every few turns and the prompt cache keeps hitting.
/// </param>
/// <param name="MaxScreenshots">Screenshots kept before old ones are dropped (each costs about 1,600 tokens).</param>
/// <param name="ScreenshotsAfterDrop">Screenshots kept (the newest) when old ones are dropped.</param>
public sealed record HistoryLimits(int MaxTurns = 40, int TrimSlack = 10, int MaxScreenshots = 6, int ScreenshotsAfterDrop = 2);

/// <summary>
/// The running conversation the next question is asked in. History only ever changes by appending,
/// except at an occasional trim (see <see cref="HistoryLimits"/>), which bumps <see cref="Generation"/>.
/// Thread-safe.
/// </summary>
/// <param name="memory">
/// What Clicky remembers about the user (M11), fixed for the conversation so the prompt prefix
/// never changes under the cache, except when the user gives feedback (see <see cref="UpdateMemory"/>).
/// Null for none.
/// </param>
public sealed class Conversation(TimeProvider? time = null, string? memory = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly List<ConversationTurn> _turns = [];

    public Guid Id { get; } = Guid.NewGuid();

    public DateTimeOffset StartedAt { get; } = (time ?? TimeProvider.System).GetUtcNow();

    public string? Memory
    {
        get
        {
            lock (_gate)
            {
                return field;
            }
        }

        private set;
    } = Normalize(memory);

    /// <summary>Bumped whenever earlier history changes (a trim), so a backend that caches it can rebuild.</summary>
    public int Generation
    {
        get
        {
            lock (_gate)
            {
                return field;
            }
        }

        private set;
    }

    public DateTimeOffset LastActivity
    {
        get
        {
            lock (_gate)
            {
                return _turns.Count > 0 ? _turns[^1].At : StartedAt;
            }
        }
    }

    public IReadOnlyList<ConversationTurn> Turns
    {
        get
        {
            lock (_gate)
            {
                return [.. _turns];
            }
        }
    }

    /// <summary>Tool calls in the last turn that the next request has to answer.</summary>
    public IReadOnlyList<ToolCallRecord> PendingToolCalls
    {
        get
        {
            lock (_gate)
            {
                return _turns.Count > 0 ? _turns[^1].ToolCalls : [];
            }
        }
    }

    /// <summary>
    /// Results of the last turn's tool calls that ran but were never sent (the agent stopped, or the
    /// model said done along with them), so the next request reports them truthfully instead of "not
    /// run". Cleared by the next <see cref="Append"/>.
    /// </summary>
    public IReadOnlyList<ToolOutcome>? UnsentResults
    {
        get
        {
            lock (_gate)
            {
                return field;
            }
        }

        private set;
    }

    public bool IsIdle(TimeSpan idle) => _time.GetUtcNow() - LastActivity >= idle;

    /// <summary>Keeps results the agent couldn't send (see <see cref="UnsentResults"/>).</summary>
    public void KeepUnsentResults(IReadOnlyList<ToolOutcome> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        lock (_gate)
        {
            UnsentResults = results.Count > 0 ? results : null;
        }
    }

    public void Append(ConversationTurn turn, HistoryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(turn);
        lock (_gate)
        {
            UnsentResults = null;
            _turns.Add(turn);
            var changed = false;
            if (_turns.Count >= limits.MaxTurns + limits.TrimSlack)
            {
                _turns.RemoveRange(0, _turns.Count - limits.MaxTurns);
                changed = true;
            }

            if (_turns.Count(t => t.Screenshot is not null) > limits.MaxScreenshots)
            {
                var keep = limits.ScreenshotsAfterDrop;
                for (var i = _turns.Count - 1; i >= 0; i--)
                {
                    if (_turns[i].Screenshot is null)
                    {
                        continue;
                    }

                    if (keep > 0)
                    {
                        keep--;
                    }
                    else
                    {
                        _turns[i] = _turns[i] with { Screenshot = null };
                    }
                }

                changed = true;
            }

            if (changed)
            {
                Generation++;
            }
        }
    }

    /// <summary>
    /// Replaces the memory block, e.g. right after the user said "you talk too much", so the next
    /// answer already follows it. Bumps <see cref="Generation"/> because the prefix changed. Returns
    /// false if nothing changed.
    /// </summary>
    public bool UpdateMemory(string? memory)
    {
        var normalized = Normalize(memory);
        lock (_gate)
        {
            if (string.Equals(Memory, normalized, StringComparison.Ordinal))
            {
                return false;
            }

            Memory = normalized;
            Generation++;
            return true;
        }
    }

    private static string? Normalize(string? memory) => string.IsNullOrWhiteSpace(memory) ? null : memory.Trim();

    /// <summary>A turn as the time provider sees now; for backends building one.</summary>
    internal DateTimeOffset Now => _time.GetUtcNow();
}
