using Clicky.Core.Buddy;
using Clicky.Core.Talk;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// Asks Claude a question and puts the answer in front of the user: the buddy thinks with the
/// question as its caption, the answer is spoken (or shown as a card) through
/// <see cref="ReplyPresenter"/>, marks go on screen, and a failure becomes a caption and a tray
/// notification. Talk uses it after speech-to-text; typed questions (M10) use it the same way.
/// </summary>
/// <remarks>Safe from any thread. Call <see cref="TalkLoop.Begin"/> when the user starts asking.</remarks>
internal sealed class TalkAnswerer(TalkLoop talk, ReplyPresenter replies, IBuddyPresence presence, IUserNotifier notifier, ILogger<TalkAnswerer> logger)
{
    public static readonly TimeSpan ProblemCaptionTime = TimeSpan.FromSeconds(8);

    /// <summary>Longest question shown as the thinking caption.</summary>
    private const int QuestionCaptionChars = 120;

    /// <summary>After each question, answered or not, on the asking thread. Onboarding's tutorial watches it.</summary>
    public event EventHandler<TalkAnswered>? Answered;

    public Task<TalkResult> AskAsync(TalkQuestion question, CancellationToken cancellationToken = default) =>
        AskAsync(question, null, cancellationToken);

    /// <summary>
    /// The same, with the answer's words going to <paramref name="beginReply"/> instead of the
    /// speaker (text mode shows them in its box). The buddy goes back to idle when it is done.
    /// </summary>
    public async Task<TalkResult> AskAsync(TalkQuestion question, Func<ITalkReply>? beginReply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        var caption = QuestionCaption(question.Text);
        presence.SetState(BuddyState.Thinking);
        presence.SetCaption(caption);

        var result = await talk.AnswerAsync(question, beginReply ?? replies.Begin, cancellationToken).ConfigureAwait(false);

        // Spoken sentences replace the question caption as they start; clear it if none did.
        ClearCaptionIf(caption);
        switch (result.Outcome)
        {
            case TalkOutcome.Answered when beginReply is not null:
                if (presence.State == BuddyState.Thinking)
                {
                    presence.SetState(BuddyState.Idle);
                }

                break;

            case TalkOutcome.Answered when result.SpokenText.Length == 0:
                if (presence.State == BuddyState.Thinking)
                {
                    presence.SetState(BuddyState.Idle);
                }

                logger.LogInformation("Talk: answered without words ({Marks} marks)", result.Marks);
                break;

            case TalkOutcome.Failed:
                if (presence.State is BuddyState.Thinking or BuddyState.Speaking)
                {
                    presence.SetState(BuddyState.Idle);
                }

                var problem = result.Problem ?? "Claude couldn't answer that.";
                ShowCaption(problem, ProblemCaptionTime);
                notifier.Notify("Clicky couldn't answer", problem, warning: true);
                break;
        }

        Answered?.Invoke(this, new TalkAnswered(question, result));
        return result;
    }

    /// <summary>Shows a caption by the buddy, then clears it unless something else replaced it meanwhile.</summary>
    public void ShowCaption(string text, TimeSpan duration)
    {
        presence.SetCaption(text);
        _ = ClearCaptionAsync(text, duration);
    }

    private static string QuestionCaption(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        if (line.Length > QuestionCaptionChars)
        {
            line = string.Concat(line.AsSpan(0, QuestionCaptionChars - 1).TrimEnd(), "…");
        }

        return $"“{line}”";
    }

    private void ClearCaptionIf(string caption)
    {
        if (string.Equals(presence.Caption, caption, StringComparison.Ordinal))
        {
            presence.SetCaption(null);
        }
    }

    private async Task ClearCaptionAsync(string caption, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        ClearCaptionIf(caption);
    }
}

internal sealed record TalkAnswered(TalkQuestion Question, TalkResult Result);
