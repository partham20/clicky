using System.Windows.Threading;
using Clicky.App.Buddy;
using Clicky.Core.Buddy;
using Clicky.Core.Voice;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// Puts replies in front of the user: spoken, with the buddy Speaking (its glow follows the voice)
/// and the current sentence as its caption; or, when Clicky has to stay quiet or the voice fails,
/// as a text card with a Copy button.
/// </summary>
/// <remarks>Safe from any thread. Only the latest reply drives the buddy.</remarks>
internal sealed class ReplyPresenter(VoiceOutput voice, IBuddyPresence presence, Dispatcher dispatcher, ILogger<ReplyPresenter> logger) : IDisposable
{
    private SpokenReply? _current;
    private ReplyCard? _card;

    /// <summary>Starts a reply whose text will stream in; append to it and complete it.</summary>
    public SpokenReply Begin()
    {
        var reply = voice.Begin();
        Volatile.Write(ref _current, reply);
        if (reply.IsSpeaking)
        {
            reply.SentenceStarted += sentence => OnSentence(reply, sentence);
            reply.LevelChanged += level => OnLevel(reply, level);
        }
        else if (presence.State == BuddyState.Thinking)
        {
            presence.SetState(BuddyState.Idle);
        }

        _ = FinishAsync(reply);
        return reply;
    }

    /// <summary>Says (or shows) a whole text.</summary>
    public SpokenReply Speak(string text)
    {
        var reply = Begin();
        reply.Append(text);
        reply.Complete();
        return reply;
    }

    /// <summary>Closes the text card, if one is showing.</summary>
    public void DismissCard() => dispatcher.InvokeAsync(() =>
    {
        _card?.Close();
        _card = null;
    });

    public void Dispose() => Volatile.Write(ref _current, null);

    private bool IsCurrent(SpokenReply reply) => ReferenceEquals(Volatile.Read(ref _current), reply);

    private void OnSentence(SpokenReply reply, string sentence)
    {
        if (!IsCurrent(reply) || presence.State == BuddyState.Listening)
        {
            return;
        }

        presence.SetState(BuddyState.Speaking);
        presence.SetCaption(sentence);
    }

    private void OnLevel(SpokenReply reply, float level)
    {
        // The mic level owns the buddy while the user talks (barge-in).
        if (IsCurrent(reply) && presence.State == BuddyState.Speaking)
        {
            presence.SetLevel(level);
        }
    }

    private async Task FinishAsync(SpokenReply reply)
    {
        var result = await reply.Completion.ConfigureAwait(false);
        if (!IsCurrent(reply))
        {
            return;
        }

        if (presence.State is BuddyState.Speaking or BuddyState.Thinking)
        {
            presence.SetLevel(0);
            presence.SetCaption(null);
            presence.SetState(BuddyState.Idle);
        }

        if (result.Outcome is ReplyOutcome.ShownAsText or ReplyOutcome.Failed && !string.IsNullOrWhiteSpace(result.Text))
        {
            logger.LogInformation("Reply shown as a card: {Reason}", result.Problem ?? result.Outcome.ToString());
            await dispatcher.InvokeAsync(() => ShowCard(result.Text.Trim(), result.Problem));
        }
    }

    private void ShowCard(string text, string? reason)
    {
        _card?.Close();
        var card = new ReplyCard(text, reason, logger);
        card.Closed += (_, _) =>
        {
            if (ReferenceEquals(_card, card))
            {
                _card = null;
            }
        };
        _card = card;
        card.Show();
    }
}
