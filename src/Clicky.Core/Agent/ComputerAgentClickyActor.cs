using Clicky.Core.Assistant;
using Clicky.Core.Clickys;

namespace Clicky.Core.Agent;

/// <summary>
/// Gives Clickys (M14) the computer-use agent. A Clicky the user set to use the computer counts as
/// permission for ordinary actions when it runs unattended; anything that sends, deletes, buys or
/// can't be undone, and moving the real mouse, is still refused because nobody is there to confirm it.
/// </summary>
public sealed class ComputerAgentClickyActor(ComputerAgent agent) : IClickyActor
{
    public bool WantsToAct(ReplyCompleted completed) => agent.WantsToAct(completed);

    public IAsyncEnumerable<ReplyEvent> ContinueAsync(
        Conversation conversation,
        ReplyCompleted completed,
        RequestScreenshot? screenshot,
        ClickyActorOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var run = new AgentRunOptions(MaxSteps: Math.Max(1, options.MaxSteps), Unattended: options.Unattended, PreApproved: options.Unattended, Tools: options.Tools);
        return agent.ContinueAsync(conversation, completed, screenshot, run, cancellationToken);
    }
}
