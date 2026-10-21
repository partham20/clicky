using Clicky.Core.Assistant;

namespace Clicky.Core.Clickys;

/// <summary>
/// The hands a Clicky works with: M13's computer-use agent behind a small seam, so runs can be
/// tested with a fake and the app works (chat only) before computer use is wired in.
/// </summary>
public interface IClickyActor
{
    /// <summary>The first reply asked to act on the computer (click, type…), so the run should continue through the actor.</summary>
    bool WantsToAct(ReplyCompleted completed);

    /// <summary>
    /// Carries out the rest of the task: executes the actions in <paramref name="completed"/> against
    /// <paramref name="screenshot"/> (the image their coordinates refer to), then loops on its own
    /// (fresh screenshot, ask Claude, act) until done, refused, cancelled or the step cap. Every
    /// step's reply events are yielded, each step ending with a <see cref="ReplyCompleted"/>; the
    /// caller pauses the run by not pulling the next event.
    /// </summary>
    IAsyncEnumerable<ReplyEvent> ContinueAsync(
        Conversation conversation,
        ReplyCompleted completed,
        RequestScreenshot? screenshot,
        ClickyActorOptions options,
        CancellationToken cancellationToken);
}

/// <param name="MaxSteps">Steps left in this run.</param>
/// <param name="Unattended">
/// Nobody may be watching (a routine, or Home in the background): no countdowns or cards; actions
/// that need permission are refused unless the actor has another way to ask.
/// </param>
/// <param name="Tools">The Clicky's own tools (routines) for this run, which the actor hands back instead of running.</param>
public sealed record ClickyActorOptions(int MaxSteps, bool Unattended = true, Agent.IRunTools? Tools = null);

/// <summary>Until computer use is available: a Clicky can chat and use connectors, but never acts on the screen.</summary>
public sealed class ChatOnlyClickyActor : IClickyActor
{
    public bool WantsToAct(ReplyCompleted completed) => false;

    public IAsyncEnumerable<ReplyEvent> ContinueAsync(
        Conversation conversation,
        ReplyCompleted completed,
        RequestScreenshot? screenshot,
        ClickyActorOptions options,
        CancellationToken cancellationToken) => AsyncEnumerable.Empty<ReplyEvent>();
}

/// <summary>Asks Claude in a given conversation; <see cref="AssistantClickyModel"/> in the app, a fake in tests.</summary>
public interface IClickyModel
{
    /// <inheritdoc cref="ClaudeAssistant.ReplyAsync(Conversation, ClaudeRequest, CancellationToken)"/>
    IAsyncEnumerable<ReplyEvent> ReplyAsync(Conversation conversation, ClaudeRequest request, CancellationToken cancellationToken);
}

/// <summary>Runs Clickys on the active backend and router, like Talk.</summary>
public sealed class AssistantClickyModel(ClaudeAssistant assistant) : IClickyModel
{
    public IAsyncEnumerable<ReplyEvent> ReplyAsync(Conversation conversation, ClaudeRequest request, CancellationToken cancellationToken) =>
        assistant.ReplyAsync(conversation, request, cancellationToken);
}

/// <summary>
/// Extra tools a Clicky's runs get on top of connectors (M15's routine tools). They are offered with
/// each request and called by <see cref="ClickyService"/> itself, between replies.
/// </summary>
public interface IClickyTools
{
    /// <summary>The tools to offer for a run of <paramref name="clicky"/>; a routine run (nobody watching) may get fewer.</summary>
    IReadOnlyList<Connectors.ConnectorTool> ToolsFor(ClickyAgent clicky, ClickyRunTrigger trigger);

    /// <summary>True if <paramref name="action"/> is a call these tools answer.</summary>
    bool Answers(Actions.ClickyAction action);

    /// <summary>Runs one call. Problems come back as error outcomes for Claude; only cancellation throws.</summary>
    /// <param name="toolCallId">The tool call it answers (API backend); null for an action tag.</param>
    Task<ToolOutcome> CallAsync(ClickyAgent clicky, ClickyRunTrigger trigger, Actions.ClickyAction action, string? toolCallId, CancellationToken cancellationToken);
}
