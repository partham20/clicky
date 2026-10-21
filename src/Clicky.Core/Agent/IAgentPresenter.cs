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

    /// <summary>Shows what the agent is doing now: "Step 3: Click "Save"".</summary>
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
/// <param name="Unattended">No card and no countdown. Permission questions go to <paramref name="Permissions"/>.</param>
/// <param name="Permissions">Who answers permission questions when unattended; null refuses everything that needs asking.</param>
public sealed record AgentRunOptions(int? MaxSteps = null, bool Unattended = false, IPermissionPrompter? Permissions = null)
{
    public static AgentRunOptions Default { get; } = new();
}
