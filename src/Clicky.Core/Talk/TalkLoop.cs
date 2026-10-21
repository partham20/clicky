using System.Text;
using Clicky.Core.Actions;
using Clicky.Core.Annotations;
using Clicky.Core.Assistant;
using Clicky.Core.Geometry;
using Clicky.Core.Ink;
using Clicky.Core.Memory;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Talk;

/// <summary>
/// The question-and-answer half of Talk (M6): memory into the prompt, the screenshot and question to
/// Claude, the answer streamed into the speaker as it arrives, its point/highlight/draw actions onto
/// the screen, the exchange into memory, and the computer-use agent (M13) when the answer asks for
/// it. Hotkeys, the mic and the buddy are the app's side (HotkeyCoordinator).
/// </summary>
/// <remarks>
/// <para>Order of a Talk: <see cref="Begin"/> when the keys go down (stops the answer in flight,
/// clears the marks, fetches memory and starts the backend warming up, which hides Claude Code's
/// 2-3 s start), then <see cref="AnswerAsync"/> once the speech is text.</para>
/// <para>Thread-safe. One answer at a time: a new question cancels the one in flight and waits for
/// its turn to be stored before asking, because a conversation takes one reply at a time.</para>
/// </remarks>
public sealed class TalkLoop : IDisposable
{
    /// <summary>Said when the model declines without a word of its own.</summary>
    public const string RefusalText = "Sorry, I can't help with that one.";

    private readonly IClaudeAssistant _assistant;
    private readonly IMemoryService _memory;
    private readonly IAnnotationRenderer _annotations;
    private readonly IInkTrailService _ink;
    private readonly IComputerAgent? _agent;
    private readonly ILogger<TalkLoop> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _disposed = new();

    private CancellationTokenSource? _answer;
    private Task _answering = Task.CompletedTask;
    private Task<string?>? _memoryPrompt;

    public TalkLoop(
        IClaudeAssistant assistant,
        IMemoryService memory,
        IAnnotationRenderer annotations,
        IInkTrailService ink,
        ILogger<TalkLoop> logger,
        IComputerAgent? agent = null,
        TimeProvider? time = null)
    {
        _assistant = assistant;
        _memory = memory;
        _annotations = annotations;
        _ink = ink;
        _logger = logger;
        _agent = agent;
        _time = time ?? TimeProvider.System;

        _assistant.ConversationEnded += OnConversationEnded;
        _memory.Updated += OnMemoryUpdated;
    }

    /// <summary>
    /// The user started asking (Talk keys down): stops the answer in flight (barge-in), clears the
    /// screen and gets the backend ready. Returns at once.
    /// </summary>
    public void Begin()
    {
        StopAnswer();
        _annotations.DismissAll();

        var prompt = LoadMemoryAsync();
        lock (_gate)
        {
            _memoryPrompt = prompt;
        }

        _ = PrepareAsync(prompt);
    }

    /// <summary>The user cancelled: stops the answer in flight and clears the screen.</summary>
    public void Cancel()
    {
        StopAnswer();
        _annotations.DismissAll();
    }

    /// <summary>Starts a new conversation (the user cleared the history).</summary>
    public void NewConversation()
    {
        Cancel();
        _assistant.NewConversation();
    }

    /// <summary>
    /// Asks Claude and plays the answer: text goes to the reply from <paramref name="beginReply"/>
    /// (called at the first word, so a failure before then never opens one), actions to the screen.
    /// Never throws for backend problems; see <see cref="TalkResult.Outcome"/>.
    /// </summary>
    public async Task<TalkResult> AnswerAsync(TalkQuestion question, Func<ITalkReply> beginReply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(beginReply);

        var answer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        Task<string?>? memoryPrompt;
        CancellationTokenSource? superseded;
        lock (_gate)
        {
            superseded = _answer;
            _answer = answer;
            previous = _answering;
            _answering = finished.Task;
            memoryPrompt = _memoryPrompt;
            _memoryPrompt = null;
        }

        TryCancel(superseded);
        try
        {
            // The superseded answer stores its (interrupted) turn on the way out; ask after that.
            await previous.ConfigureAwait(false);
            return await RunAsync(question, beginReply, memoryPrompt ?? LoadMemoryAsync(), answer.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_answer, answer))
                {
                    _answer = null;
                }
            }

            answer.Dispose();
            finished.SetResult();
        }
    }

    public void Dispose()
    {
        _assistant.ConversationEnded -= OnConversationEnded;
        _memory.Updated -= OnMemoryUpdated;
        TryCancel(_disposed);
    }

    private static void TryCancel(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // That answer just finished.
        }
    }

    private static MemoryTurnMode ModeFor(RequestSource source) => source switch
    {
        RequestSource.Typed => MemoryTurnMode.Text,
        RequestSource.Agent => MemoryTurnMode.Agent,
        _ => MemoryTurnMode.Voice,
    };

    /// <summary>The screenshot as Claude gets it, with the pointer if it was on the image.</summary>
    private static RequestScreenshot? ToScreenshot(TalkQuestion question)
    {
        if (question.Capture is not { } capture)
        {
            return null;
        }

        var geometry = capture.Geometry;
        ScreenshotPoint? pointer = null;
        if (question.Cursor is { } cursor && geometry.Source.Contains(cursor))
        {
            pointer = CoordinateMapper.ToScreenshot(cursor, geometry);
        }

        return new RequestScreenshot(capture.Png, geometry, pointer);
    }

    private void StopAnswer()
    {
        CancellationTokenSource? answer;
        lock (_gate)
        {
            answer = _answer;
        }

        TryCancel(answer);
    }

    private async Task<TalkResult> RunAsync(TalkQuestion question, Func<ITalkReply> beginReply, Task<string?> memoryPrompt, CancellationToken cancellationToken)
    {
        var memory = await memoryPrompt.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var conversation = _assistant.CurrentConversation(memory);
        var screenshot = await WithInkAsync(ToScreenshot(question), question.Ink, cancellationToken).ConfigureAwait(false);
        var request = new ClaudeRequest(question.Text, question.Source, screenshot);
        var answer = new Answer(beginReply, screenshot?.Geometry);

        try
        {
            ReplyCompleted? completed = null;
            await foreach (var replyEvent in _assistant.ReplyAsync(conversation, request, cancellationToken).ConfigureAwait(false))
            {
                completed = Handle(replyEvent, answer) ?? completed;
            }

            // The agent stores its own (Agent) memory turn; this one is the user's question and the first answer.
            var answered = answer.Spoken.ToString();
            if (completed is not null && _agent is not null && _agent.WantsToAct(completed))
            {
                _logger.LogInformation("Talk: the answer asks for action; handing over to the computer-use agent");
                await foreach (var replyEvent in _agent.ContinueAsync(conversation, completed, screenshot, cancellationToken).ConfigureAwait(false))
                {
                    completed = Handle(replyEvent, answer) ?? completed;
                }
            }

            if (completed?.Stop == ReplyStop.Refused && answer.Spoken.Length == 0)
            {
                answer.Say(RefusalText);
            }

            answer.Reply?.Complete();
            Record(question, answered.Length > 0 ? answered : answer.Spoken.ToString());
            _logger.LogInformation(
                "Talk answered: {Chars} chars, {Marks} marks, stop {Stop}",
                answer.Spoken.Length,
                answer.Marks,
                completed?.Stop.ToString() ?? "none");
            return new TalkResult(TalkOutcome.Answered, answer.Spoken.ToString(), answer.Marks, completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            answer.Reply?.Interrupt();
            if (answer.Spoken.Length > 0)
            {
                Record(question, answer.Spoken.ToString());
            }

            _logger.LogInformation("Talk answer cut off after {Chars} chars", answer.Spoken.Length);
            return new TalkResult(TalkOutcome.Cancelled, answer.Spoken.ToString(), answer.Marks);
        }
        catch (ClaudeBackendException ex)
        {
            // Whatever was said before the failure stays said; the problem is shown separately.
            answer.Reply?.Complete();
            _logger.LogWarning("Talk: Claude failed ({Failure}): {Message}", ex.Failure, ex.Message);
            return new TalkResult(TalkOutcome.Failed, answer.Spoken.ToString(), answer.Marks, Problem: ex.Message, Failure: ex.Failure);
        }
    }

    /// <summary>Routes one event; returns it when it is the final one.</summary>
    private ReplyCompleted? Handle(ReplyEvent replyEvent, Answer answer)
    {
        switch (replyEvent)
        {
            case ReplyStarted started:
                _logger.LogDebug("Talk: {Backend} {Model} ({Tier}) answering", started.Backend, started.Model, started.Tier);
                break;

            case ReplyText text:
                answer.Say(text.Text);
                break;

            case ReplyAction { Action: SayAction }:
                // Speech already streamed as text.
                break;

            case ReplyAction action:
                Show(action.Action, answer);
                break;

            case ReplyActionRejected rejected:
                _logger.LogInformation("Talk: Claude's {Action} was dropped: {Reason}", rejected.Name, rejected.Reason);
                break;

            case ReplyScreenshotTaken taken:
                answer.Geometry = taken.Screenshot.Geometry;
                break;

            case ReplyCompleted completed:
                return completed;
        }

        return null;
    }

    private void Show(ClickyAction action, Answer answer)
    {
        if (answer.Geometry is not { } geometry)
        {
            _logger.LogInformation("Talk: {Action} has no screenshot to refer to; not shown", action.Name);
            return;
        }

        try
        {
            if (_annotations.Show(action, geometry) is not null)
            {
                answer.Marks++;
            }
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Talk: could not show {Action}", action.Name);
        }
    }

    private void Record(TalkQuestion question, string spoken)
    {
        try
        {
            _memory.RecordTurn(new MemoryTurn(question.Text, spoken.Length > 0 ? spoken : null, _time.GetUtcNow(), ModeFor(question.Source)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Talk: could not queue the exchange for memory");
        }
    }

    /// <summary>Paints and describes the pointer's trail on the screenshot (M9); without it if that fails.</summary>
    private async Task<RequestScreenshot?> WithInkAsync(RequestScreenshot? screenshot, InkTrail? trail, CancellationToken cancellationToken)
    {
        if (screenshot is null || trail is null || trail.IsEmpty)
        {
            return screenshot;
        }

        try
        {
            return await _ink.AttachAsync(screenshot, trail, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            _logger.LogWarning(ex, "Talk: could not attach the ink trail; asking without it");
            return screenshot;
        }
    }

    private async Task<string?> LoadMemoryAsync()
    {
        try
        {
            var prompt = await _memory.GetPromptAsync(_disposed.Token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(prompt) ? null : prompt;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Talk: could not read memory; asking without it");
            }

            return null;
        }
    }

    private async Task PrepareAsync(Task<string?> prompt)
    {
        var memory = await prompt.ConfigureAwait(false);
        _assistant.Prepare(memory);
    }

    private void OnConversationEnded(object? sender, Conversation conversation) => _ = EndConversationAsync(conversation);

    private async Task EndConversationAsync(Conversation conversation)
    {
        try
        {
            await _memory.EndConversationAsync(_disposed.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Talk: memory pass for conversation {Conversation} failed", conversation.Id);
        }
    }

    private void OnMemoryUpdated(object? sender, MemoryUpdatedEventArgs e)
    {
        // Feedback ("you talk too much") applies from the very next answer, not the next conversation.
        if (e.FromFeedback)
        {
            _ = RefreshMemoryAsync();
        }
    }

    private async Task RefreshMemoryAsync()
    {
        var prompt = await LoadMemoryAsync().ConfigureAwait(false);
        if (_assistant.CurrentConversation(prompt).UpdateMemory(prompt))
        {
            _logger.LogInformation("Talk: memory changed after feedback; the next answer uses it");
        }
    }

    /// <summary>One answer in progress.</summary>
    private sealed class Answer(Func<ITalkReply> beginReply, ScreenshotGeometry? geometry)
    {
        public ITalkReply? Reply { get; private set; }

        public StringBuilder Spoken { get; } = new();

        public ScreenshotGeometry? Geometry { get; set; } = geometry;

        public int Marks { get; set; }

        public void Say(string text)
        {
            if (text.Length == 0)
            {
                return;
            }

            Reply ??= beginReply();
            Reply.Append(text);
            Spoken.Append(text);
        }
    }
}
