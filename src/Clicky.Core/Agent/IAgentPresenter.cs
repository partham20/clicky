using Clicky.Core.Geometry;

namespace Clicky.Core.Agent;

/// <summary>Shows a running agent: the agent card with its step, countdown, permission prompt and Stop button.</summary>
public interface IAgentPresenter
{
    /// <summary>Shows a card for a new run. Any thread.</summary>
    /// <param name="task">What the user asked for, short.</param>
    /// <param name="near">The screen area being worked on (physical pixels), so the card shows on that monitor.</param>
    IAgentCard Begin(string task, PixelRect? near);
}

/// <summary>One run's card. Every member is safe from any thread. Dispose closes it.</summary>
public interface IAgentCard : IPermissionPrompter, IDisposable
{
    /// <summary>Cancelled when the user presses Stop.</summary>
    CancellationToken StopRequested { get; }

    /// <summary>Shows what the agent is doing now: "Step 3: Click "Save"". Number 0 shows the text alone.</summary>
    void ShowStep(int number, string text);

    /// <summary>
    /// "About to: …" with a countdown and Cancel. True when the time ran out (go ahead), false when
    /// the user cancelled.
    /// </summary>
    Task<bool> CountdownAsync(string upcoming, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Moves the card out of the way if it covers <paramref name="point"/> (physical pixels), so a click can't land on it.</summary>
    void Avoid(PixelPoint point);

    /// <summary>The run ended; the card shows this briefly, then closes.</summary>
    void Finish(string message, bool success);
}

/// <summary>Options for a run that nobody is watching (M14 Clickys, M15 routines).</summary>
/// <param name="MaxSteps">Overrides settings.agent.maxSteps.</param>
/// <param name="Unattended">
/// No card, no countdown and no memory turn (the caller keeps its own record). Permission questions
/// go to <paramref name="Permissions"/>.
/// </param>
/// <param name="Permissions">Who answers permission questions when unattended; null refuses everything that needs asking.</param>
/// <param name="PreApproved">
/// The user already allowed acting for this run (a Clicky set to use the computer): ordinary actions
/// and connector tools run without asking. Anything that sends, deletes, buys or can't be undone is
/// still asked about, and moving the real mouse is too.
/// </param>
/// <param name="Tools">Tools the caller answers itself (a Clicky's routine tools): offered on every step and handed back, never asked about here.</param>
public sealed record AgentRunOptions(int? MaxSteps = null, bool Unattended = false, IPermissionPrompter? Permissions = null, bool PreApproved = false, IRunTools? Tools = null)
{
    public static AgentRunOptions Default { get; } = new();
}

/// <summary>
/// Tools for one run that its caller answers, such as a Clicky's routine tools (M15). They do their
/// own asking (the routine card), so the agent neither gates nor counts down for them.
/// </summary>
public interface IRunTools
{
    /// <summary>Offered with every step's request, like connector tools.</summary>
    IReadOnlyList<Connectors.ConnectorTool> Offered { get; }

    /// <summary>True if <paramref name="action"/> is a call these tools answer.</summary>
    bool Answers(Actions.ClickyAction action);

    /// <summary>Runs one call. Problems come back as error outcomes; only cancellation throws.</summary>
    Task<Assistant.ToolOutcome> CallAsync(Actions.ClickyAction action, string? toolCallId, CancellationToken cancellationToken);
}
