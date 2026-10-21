using Clicky.Core.Actions;
using Clicky.Core.Annotations;
using Clicky.Core.Assistant;
using Clicky.Core.Buddy;
using Clicky.Core.Capture;
using Clicky.Core.Settings;
using Clicky.Core.Skills;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Walkthroughs;

/// <summary>The spoken part of one reply, as it streams (the app's reply presenter behind it).</summary>
public interface IReplySpeech
{
    void Append(string text);

    /// <summary>No more text is coming; the rest is still said.</summary>
    void Complete();

    /// <summary>Stops at once.</summary>
    void Interrupt();
}

/// <summary>
/// The stock way a walkthrough asks for its next step: a screenshot taken the way settings.capture
/// says, the app guides for the app in front, a request in the current conversation, and the reply
/// spoken and drawn (pointing and the like here; steps by the walkthrough engine).
/// </summary>
/// <param name="conversation">The conversation the walkthrough is part of (the assistant's current one).</param>
/// <param name="ask">Streams a reply (<see cref="ClaudeAssistant.ReplyAsync(Conversation, ClaudeRequest, CancellationToken)"/>).</param>
/// <param name="speak">Starts a spoken reply.</param>
public sealed class AssistantWalkthroughTurns(
    ICaptureService capture,
    ISettingsStore settings,
    IAnnotationRenderer renderer,
    IBuddyPresence presence,
    IAppSkills skills,
    IForegroundApp foreground,
    Func<Conversation> conversation,
    Func<Conversation, ClaudeRequest, CancellationToken, IAsyncEnumerable<ReplyEvent>> ask,
    Func<IReplySpeech> speak,
    ILogger<AssistantWalkthroughTurns> logger) : IWalkthroughTurns
{
    public async Task RunAsync(WalkthroughTurn turn, IWalkthroughs walkthroughs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(walkthroughs);
        if (presence.State != BuddyState.Listening)
        {
            presence.SetState(BuddyState.Thinking);
        }

        IReplySpeech? speech = null;
        try
        {
            var shot = await capture.CaptureAsync(new CaptureTarget(settings.Current.Capture.Mode), cancellationToken).ConfigureAwait(false);
            var current = conversation();
            var app = foreground.Current();
            var request = new ClaudeRequest(
                turn.Prompt,
                RequestSource.Walkthrough,
                new RequestScreenshot(shot.Image, shot.Geometry),
                Context: skills.ContextFor(current, app));
            logger.LogDebug("Walkthrough turn after step {Index} ({Reason}) in {App}, screenshot {Geometry}", turn.StepIndex, turn.Reason, app, shot.Geometry);

            await foreach (var replyEvent in ask(current, request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                switch (replyEvent)
                {
                    case ReplyText { Text.Length: > 0 } text:
                        (speech ??= speak()).Append(text.Text);
                        break;
                    case ReplyAction { Action: not StepAction } action when AnnotationPlanner.Draws(action.Action):
                        renderer.Show(action.Action, shot.Geometry);
                        break;
                }

                walkthroughs.Observe(replyEvent, shot.Geometry);
            }

            speech?.Complete();
        }
        catch
        {
            speech?.Interrupt();
            throw;
        }
        finally
        {
            // Nothing was said (cancelled, failed, or a reply without words): don't leave the buddy thinking.
            if (speech is null && presence.State == BuddyState.Thinking)
            {
                presence.SetState(BuddyState.Idle);
            }
        }
    }
}
