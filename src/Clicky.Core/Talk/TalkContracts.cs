using Clicky.Core.Assistant;
using Clicky.Core.Capture;
using Clicky.Core.Geometry;

namespace Clicky.Core.Talk;

/// <summary>
/// Where an answer's words go as they stream in: the sentence speaker, or a text card when Clicky
/// has to stay quiet. <see cref="Voice.SpokenReply"/> is the real one.
/// </summary>
public interface ITalkReply
{
    /// <summary>More reply text (any size).</summary>
    void Append(string text);

    /// <summary>No more text is coming.</summary>
    void Complete();

    /// <summary>Stops it; returns true if it was still going.</summary>
    bool Interrupt();
}

/// <summary>
/// The computer-use agent (M13), as the Talk loop sees it. After an answer completes, the loop asks
/// <see cref="WantsToAct"/>; if so, it streams <see cref="ContinueAsync"/> into the same reply and
/// draws its actions. The agent runs clicks, typing and connector calls (behind the permission gate),
/// answers them in the conversation and records its own memory turn. Register one in DI and the
/// loop picks it up.
/// </summary>
public interface IComputerAgent
{
    /// <summary>True if <paramref name="completed"/> asked for something only the agent can do (click, type, a connector call…).</summary>
    bool WantsToAct(ReplyCompleted completed);

    /// <summary>
    /// Carries on from <paramref name="completed"/> until the task is done, cancelled or handed back.
    /// Yields the same events as a backend; a <see cref="ReplyScreenshotTaken"/> before any actions
    /// that refer to a new screenshot.
    /// </summary>
    /// <param name="screenshot">The screenshot the question came with, if any.</param>
    IAsyncEnumerable<ReplyEvent> ContinueAsync(Conversation conversation, ReplyCompleted completed, RequestScreenshot? screenshot, CancellationToken cancellationToken);
}

/// <summary>One question for the Talk loop.</summary>
/// <param name="Text">What the user said (transcript) or typed.</param>
/// <param name="Capture">The screenshot taken when they asked; null if capture failed.</param>
/// <param name="Cursor">Where the pointer was when they asked, in physical pixels.</param>
public sealed record TalkQuestion(string Text, RequestSource Source, CaptureResult? Capture = null, PixelPoint? Cursor = null);

public enum TalkOutcome
{
    /// <summary>Claude answered (possibly with nothing to say, only marks).</summary>
    Answered,

    /// <summary>A newer question, barge-in or shutdown cut it off.</summary>
    Cancelled,

    /// <summary>The backend failed; <see cref="TalkResult.Problem"/> says why, for the user.</summary>
    Failed,
}

/// <param name="SpokenText">Everything the answer said (also when cancelled part way).</param>
/// <param name="Marks">Point, highlight, draw and step marks put on screen.</param>
/// <param name="Completed">The backend's (or the agent's) final event, when it got that far.</param>
/// <param name="Problem">User-facing reason for <see cref="TalkOutcome.Failed"/>.</param>
public sealed record TalkResult(
    TalkOutcome Outcome,
    string SpokenText,
    int Marks,
    ReplyCompleted? Completed = null,
    string? Problem = null,
    ClaudeFailure? Failure = null);
