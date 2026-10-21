using Clicky.Core.Assistant;
using Clicky.Core.Geometry;

namespace Clicky.Core.Walkthroughs;

public enum WalkthroughPhase
{
    /// <summary>The current step is on screen; Clicky waits for the user to do it.</summary>
    Waiting,

    /// <summary>The user did it; Clicky lets the app react before taking a new screenshot.</summary>
    Settling,

    /// <summary>Asking Claude for the next step.</summary>
    Thinking,

    /// <summary>Over; see <see cref="WalkthroughSnapshot.End"/>.</summary>
    Finished,
}

public enum WalkthroughEnd
{
    /// <summary>Claude said the task is done.</summary>
    Completed,

    /// <summary>The user stopped it.</summary>
    Cancelled,

    /// <summary>Hit <see cref="WalkthroughOptions.MaxSteps"/>.</summary>
    LimitReached,

    /// <summary>No progress for <see cref="WalkthroughOptions.IdleTimeout"/>.</summary>
    TimedOut,

    /// <summary>A new walkthrough (a step 1) started in its place.</summary>
    Replaced,
}

public enum WalkthroughStepStatus
{
    Current,
    Done,
    Skipped,
}

/// <param name="Index">1-based, as the model numbered it.</param>
/// <param name="HasTarget">The step has a spot on screen, so Clicky can tell when it is done.</param>
public sealed record WalkthroughStepInfo(int Index, string Instruction, bool HasTarget, WalkthroughStepStatus Status);

/// <summary>A walkthrough as the step card shows it. Immutable.</summary>
/// <param name="Total">The model's estimate of how many steps there are, if it gave one.</param>
/// <param name="Steps">Steps so far, in order; the last is the current one unless it finished.</param>
/// <param name="Problem">One line for the card when the next step could not be fetched (Retry fixes it).</param>
public sealed record WalkthroughSnapshot(
    Guid Id,
    WalkthroughPhase Phase,
    int? Total,
    IReadOnlyList<WalkthroughStepInfo> Steps,
    WalkthroughEnd? End = null,
    string? Problem = null)
{
    public WalkthroughStepInfo? Current => Steps.Count > 0 && Steps[^1].Status == WalkthroughStepStatus.Current ? Steps[^1] : null;

    public bool CanGoBack => Phase != WalkthroughPhase.Finished && Steps.Count > 1;

    public bool IsActive => Phase != WalkthroughPhase.Finished;

    /// <summary>"Step 2 of 5", with the total never below the step number.</summary>
    public string Progress
    {
        get
        {
            var index = Steps.Count > 0 ? Steps[^1].Index : 0;
            return Total is { } total ? $"Step {index} of {Math.Max(total, index)}" : $"Step {index}";
        }
    }
}

public sealed class WalkthroughChangedEventArgs(WalkthroughSnapshot snapshot) : EventArgs
{
    /// <summary>The walkthrough as it is now; <see cref="WalkthroughSnapshot.Phase"/> is Finished once it ends.</summary>
    public WalkthroughSnapshot Snapshot { get; } = snapshot;
}

/// <summary>
/// Step-by-step walkthroughs: shows one step at a time, notices when the user has done it (a click on
/// the spot, or the element there being focused or pressed), takes a new screenshot and asks Claude for
/// the next one, up to <see cref="WalkthroughOptions.MaxSteps"/>. Takes a new look when the user goes
/// another way; the user can go back, skip or stop from the step card.
/// </summary>
/// <remarks>
/// <para>The talk loop passes it every reply event (<see cref="Observe"/>) and leaves step actions to it:
/// it draws them, so the talk loop must not send them to the annotation renderer as well. Everything
/// else in a reply (speech, pointing) stays with the talk loop.</para>
/// <para>All members are safe from any thread and return at once; the work runs on a queue of its own.</para>
/// </remarks>
public interface IWalkthroughs
{
    /// <summary>The walkthrough in progress, or null.</summary>
    WalkthroughSnapshot? Current { get; }

    /// <summary>Raised on a background thread whenever the walkthrough changes, and once more when it finishes.</summary>
    event EventHandler<WalkthroughChangedEventArgs>? Changed;

    /// <summary>
    /// Feed it every event of every reply, in order, with the geometry of the screenshot the request
    /// carried (null if it had none). A step action starts or advances a walkthrough; a done action, or a
    /// walkthrough turn that ends without a step, finishes it.
    /// </summary>
    void Observe(ReplyEvent replyEvent, ScreenshotGeometry? geometry);

    /// <summary>The user says they did the current step (for steps Clicky can't see being done).</summary>
    void MarkDone();

    void Skip();

    /// <summary>Shows the previous step again.</summary>
    void Back();

    /// <summary>Asks for the next step again after it failed.</summary>
    void Retry();

    /// <summary>Ends the walkthrough and takes its marks down.</summary>
    void Cancel();
}

public enum WalkthroughTurnReason
{
    /// <summary>The user did the step.</summary>
    StepDone,

    /// <summary>The user skipped it.</summary>
    StepSkipped,

    /// <summary>The user went another way; the screen needs a new look.</summary>
    OffTrack,

    /// <summary>The last allowed step is done: wrap up.</summary>
    LimitReached,
}

/// <summary>One request Clicky makes on its own during a walkthrough.</summary>
/// <param name="Prompt">What to send as the request text (RequestSource.Walkthrough).</param>
/// <param name="StepIndex">The step it follows from.</param>
public sealed record WalkthroughTurn(Guid WalkthroughId, WalkthroughTurnReason Reason, string Prompt, int StepIndex);

/// <summary>
/// Runs a walkthrough's own requests: a fresh screenshot, Claude, the reply spoken and drawn, every
/// reply event passed back through <see cref="IWalkthroughs.Observe"/> with that screenshot's geometry.
/// <see cref="AssistantWalkthroughTurns"/> is the stock one; the talk loop can register its own to run
/// them through the same pipeline as spoken questions.
/// </summary>
public interface IWalkthroughTurns
{
    /// <exception cref="OperationCanceledException">The user started talking, or the walkthrough ended.</exception>
    /// <exception cref="ClaudeBackendException">Claude could not answer.</exception>
    Task RunAsync(WalkthroughTurn turn, IWalkthroughs walkthroughs, CancellationToken cancellationToken);
}
