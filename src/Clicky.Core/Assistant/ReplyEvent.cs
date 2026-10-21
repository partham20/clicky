using Clicky.Core.Actions;

namespace Clicky.Core.Assistant;

/// <summary>
/// What a backend streams back while it answers. Every backend produces the same events, in order:
/// one <see cref="ReplyStarted"/>, then <see cref="ReplyText"/> and <see cref="ReplyAction"/> as they
/// arrive (interleaved the way the model wrote them), then one <see cref="ReplyCompleted"/>.
/// </summary>
public abstract record ReplyEvent;

/// <param name="Reason">Why the router picked this tier, for the log.</param>
public sealed record ReplyStarted(ClaudeBackendKind Backend, string Model, ModelTier Tier, string Reason) : ReplyEvent;

/// <summary>Spoken text as it streams; feed it to the sentence speaker.</summary>
public sealed record ReplyText(string Text) : ReplyEvent;

/// <summary>A validated non-speech action (point, highlight, …), already checked against the screenshot.</summary>
public sealed record ReplyAction(ClickyAction Action) : ReplyEvent;

/// <summary>The model produced an action that failed validation; it was dropped and the reply went on.</summary>
public sealed record ReplyActionRejected(string Name, string Reason) : ReplyEvent;

public enum ReplyStop
{
    /// <summary>The model finished.</summary>
    EndTurn,

    /// <summary>The model finished with actions it may want results for (agent work).</summary>
    Actions,

    /// <summary>Cut off at the token limit.</summary>
    MaxTokens,

    /// <summary>The model declined for policy reasons.</summary>
    Refused,
}

public sealed record ReplyUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, decimal? CostUsd = null)
{
    public static ReplyUsage None { get; } = new(0, 0, 0, 0);
}

/// <param name="Actions">
/// The whole reply as actions in order, speech included as <see cref="SayAction"/>s, for consumers
/// that don't stream (logs, tests, the agent loop).
/// </param>
/// <param name="FirstText">Time from the request to the first spoken text, if there was any.</param>
/// <param name="Model">The model that actually answered (a fallback may differ from the one asked for).</param>
public sealed record ReplyCompleted(
    ReplyStop Stop,
    IReadOnlyList<ClickyAction> Actions,
    ReplyUsage Usage,
    TimeSpan? FirstText,
    TimeSpan Elapsed,
    string Model,
    string? Detail = null) : ReplyEvent
{
    /// <summary>Everything the reply said, joined.</summary>
    public string SpokenText => string.Concat(Actions.OfType<SayAction>().Select(a => a.Text));
}
