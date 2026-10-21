using System.Runtime.CompilerServices;
using Clicky.Core.Assistant.ClaudeCode;
using Clicky.Core.Memory;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Assistant;

/// <summary>What the Talk loop needs from <see cref="ClaudeAssistant"/>; an interface so it can be tested without a backend.</summary>
public interface IClaudeAssistant
{
    /// <summary>Raised (on the calling thread) when a conversation is dropped: replaced after going idle, or <see cref="NewConversation"/>.</summary>
    event EventHandler<Conversation>? ConversationEnded;

    /// <summary>The conversation the next question belongs to; <paramref name="memory"/> is used only when a new one starts.</summary>
    Conversation CurrentConversation(string? memory = null);

    /// <summary>Forgets the conversation; the next question starts fresh.</summary>
    void NewConversation();

    /// <summary>The user pressed Talk: let the active backend get ready. Never throws.</summary>
    void Prepare(string? memory = null);

    /// <summary>Routes the request, then streams the active backend's reply; the first event is <see cref="ReplyStarted"/>.</summary>
    /// <exception cref="ClaudeBackendException">Not set up, or the request failed.</exception>
    IAsyncEnumerable<ReplyEvent> ReplyAsync(Conversation conversation, ClaudeRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The one place the rest of the app asks Claude: picks the backend from settings, routes each
/// question to the fast or deep model, and keeps the running conversation (a new one starts after
/// settings.claude.idleResetMinutes of quiet). Also the memory updater's model, on the active
/// backend's fast model.
/// </summary>
public sealed class ClaudeAssistant(
    ISettingsStore settings,
    AnthropicApiBackend api,
    ClaudeCodeBackend subscription,
    ILogger<ClaudeAssistant> logger,
    TimeProvider? time = null) : IClaudeAssistant, IMemoryModel
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private Conversation? _conversation;

    public event EventHandler<Conversation>? ConversationEnded;

    /// <summary>The backend settings.claude.backend selects.</summary>
    public IClaudeBackend Active => Backend(settings.Current.Claude.Backend);

    public IClaudeBackend Backend(ClaudeBackendKind kind) => kind == ClaudeBackendKind.Subscription ? subscription : api;

    /// <summary>
    /// The conversation the next question belongs to: the current one, or a new one if it has been
    /// idle too long. <paramref name="memory"/> is used only when a new one starts.
    /// </summary>
    public Conversation CurrentConversation(string? memory = null)
    {
        var idle = TimeSpan.FromMinutes(settings.Current.Claude.IdleResetMinutes);
        Conversation? ended = null;
        Conversation current;
        lock (_gate)
        {
            if (_conversation is null || _conversation.IsIdle(idle))
            {
                if (_conversation is not null)
                {
                    logger.LogInformation("Conversation {Conversation} idle for {Minutes} min; starting a new one", _conversation.Id, idle.TotalMinutes);
                    ended = _conversation;
                }

                _conversation = new Conversation(_time, memory);
            }

            current = _conversation;
        }

        if (ended is not null)
        {
            ConversationEnded?.Invoke(this, ended);
        }

        return current;
    }

    /// <summary>Forgets the conversation; the next question starts fresh.</summary>
    public void NewConversation()
    {
        Conversation? ended;
        lock (_gate)
        {
            ended = _conversation;
            _conversation = null;
        }

        if (ended is not null)
        {
            logger.LogInformation("Conversation {Conversation} cleared", ended.Id);
            ConversationEnded?.Invoke(this, ended);
        }
    }

    /// <summary>The user pressed Talk: let the active backend get ready. Never throws.</summary>
    public void Prepare(string? memory = null) => Active.Prepare(CurrentConversation(memory));

    /// <summary>Answers in the current conversation (see <see cref="ReplyAsync(Conversation, ClaudeRequest, CancellationToken)"/>).</summary>
    public IAsyncEnumerable<ReplyEvent> ReplyAsync(ClaudeRequest request, CancellationToken cancellationToken = default) =>
        ReplyAsync(CurrentConversation(), request, cancellationToken);

    /// <summary>Routes the request, then streams the active backend's reply; the first event is <see cref="ReplyStarted"/>.</summary>
    /// <exception cref="ClaudeBackendException">Not set up, or the request failed.</exception>
    public async IAsyncEnumerable<ReplyEvent> ReplyAsync(Conversation conversation, ClaudeRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var claude = settings.Current.Claude;
        var route = ModelRouter.Route(request, claude);
        var model = claude.ModelFor(route.Tier);
        var backend = Backend(claude.Backend);
        logger.LogInformation("Asking Claude ({Backend}, {Model}, {Tier}: {Reason})", backend.Kind, model, route.Tier, route.Reason);
        yield return new ReplyStarted(backend.Kind, model, route.Tier, route.Reason);

        await foreach (var replyEvent in backend.ReplyAsync(conversation, request, model, claude.EffortFor(route.Tier), claude.ThinkingFor(route.Tier), cancellationToken).ConfigureAwait(false))
        {
            yield return replyEvent;
        }
    }

    /// <summary>Memory updates run on the active backend's fast model.</summary>
    public Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken)
    {
        var claude = settings.Current.Claude;
        return Backend(claude.Backend).CompleteAsync(system, user, claude.ModelFor(ModelTier.Fast), cancellationToken);
    }
}
