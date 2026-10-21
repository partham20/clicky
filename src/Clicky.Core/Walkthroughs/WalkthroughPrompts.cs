using System.Globalization;

namespace Clicky.Core.Walkthroughs;

/// <summary>
/// The request texts Clicky sends on its own during a walkthrough. They start with [Walkthrough] so
/// the model (and the system prompt's walkthrough rules) can tell them from the user's words.
/// </summary>
public static class WalkthroughPrompts
{
    /// <param name="index">The step that was on screen.</param>
    /// <param name="instruction">Its instruction, as the model wrote it.</param>
    /// <param name="maxSteps">The step limit.</param>
    /// <param name="detail">For <see cref="WalkthroughTurnReason.OffTrack"/>: what the user did instead.</param>
    public static string For(WalkthroughTurnReason reason, int index, string instruction, int maxSteps, string? detail = null)
    {
        var step = string.Create(CultureInfo.InvariantCulture, $"step {index} (\"{instruction}\")");
        var next = index + 1;
        return reason switch
        {
            WalkthroughTurnReason.StepDone => string.Create(
                CultureInfo.InvariantCulture,
                $"[Walkthrough] The user just did {step}. The screenshot shows the screen now. If the task is finished, say so in one short sentence and use done. Otherwise give step {next}: one short spoken sentence and a step action pointing at exactly where to act on this screenshot."),
            WalkthroughTurnReason.StepSkipped => string.Create(
                CultureInfo.InvariantCulture,
                $"[Walkthrough] The user skipped {step}; they may or may not have done it. Look at the screenshot and give the step they should do now as step {next}, or use done if the task is already finished."),
            WalkthroughTurnReason.OffTrack => string.Create(
                CultureInfo.InvariantCulture,
                $"[Walkthrough] The user was on {step} but {detail ?? "did something else"}. Look at the new screenshot. If they did the step another way, give step {next}. If they went off track, say briefly how to get back and give the step to do now, numbered {index}. If the task is finished, use done."),
            WalkthroughTurnReason.LimitReached => string.Create(
                CultureInfo.InvariantCulture,
                $"[Walkthrough] The user just did {step}, the most a walkthrough can have ({maxSteps}). Don't give another step: in a sentence or two, tell them what is left to do (if anything), then use done."),
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
        };
    }
}
