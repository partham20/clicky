using System.Runtime.CompilerServices;
using Clicky.Core.Actions;
using Clicky.Core.Annotations;
using Clicky.Core.Assistant;
using Clicky.Core.Capture;
using Clicky.Core.Geometry;
using Clicky.Core.Memory;
using Clicky.Core.Talk;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Talk;

public sealed class TalkLoopTests : IDisposable
{
    // A 2560x1440 monitor at (-2560, 0), captured at half size.
    private static readonly ScreenshotGeometry Geometry = new(new PixelRect(-2560, 0, 0, 1440), 1280, 720);
    private static readonly CaptureResult Capture = new([1, 2, 3], Geometry, null, TimeSpan.FromMilliseconds(120));

    private readonly FakeAssistant _assistant = new();
    private readonly FakeMemory _memory = new();
    private readonly FakeAnnotations _annotations = new();
    private readonly List<FakeReply> _replies = [];
    private FakeAgent? _agent;
    private TalkLoop? _loop;

    private TalkLoop Loop => _loop ??= new TalkLoop(_assistant, _memory, _annotations, NullLogger<TalkLoop>.Instance, _agent);

    public void Dispose() => _loop?.Dispose();

    [Fact]
    public async Task Answer_streams_into_the_reply_draws_marks_and_is_remembered()
    {
        _memory.Prompt = "<memory>Prefers short answers.</memory>";
        _assistant.Script = (_, _, _) => Events(
            new ReplyStarted(ClaudeBackendKind.Api, "claude-haiku-5-5", ModelTier.Fast, "short question"),
            new ReplyText("It's the "),
            new ReplyText("blue button."),
            new ReplyAction(new PointAction(640, 360, "Share")),
            new ReplyAction(new ClickAction(1, 1)),
            Completed(ReplyStop.EndTurn, new SayAction("It's the blue button."), new PointAction(640, 360, "Share")));

        Loop.Begin();
        var result = await Loop.AnswerAsync(Question("where's share?", new PixelPoint(-1280, 720)), Begin);

        Assert.Equal(TalkOutcome.Answered, result.Outcome);
        Assert.Equal("It's the blue button.", result.SpokenText);
        var reply = Assert.Single(_replies);
        Assert.Equal("It's the blue button.", reply.Text);
        Assert.True(reply.Completed);

        // The point is drawn against the question's screenshot; a click without an agent draws nothing.
        Assert.Equal(1, result.Marks);
        var (action, geometry) = _annotations.Shown[0];
        Assert.IsType<PointAction>(action);
        Assert.Same(Geometry, geometry);
        Assert.Equal(1, _annotations.Dismissals);

        // Memory went into the new conversation; the pointer is mapped onto the screenshot.
        Assert.Equal("<memory>Prefers short answers.</memory>", _assistant.Conversation!.Memory);
        Assert.Equal(["<memory>Prefers short answers.</memory>"], _assistant.Prepared);
        var request = Assert.Single(_assistant.Requests);
        Assert.Equal(RequestSource.Voice, request.Source);
        Assert.Equal(CoordinateMapper.ToScreenshot(new PixelPoint(-1280, 720), Geometry), request.Screenshot!.Cursor);
        Assert.Equal(639.75, request.Screenshot.Cursor!.Value.X);
        Assert.Equal([1, 2, 3], request.Screenshot.Png);

        var turn = Assert.Single(_memory.Turns);
        Assert.Equal(("where's share?", "It's the blue button.", MemoryTurnMode.Voice), (turn.User, turn.Assistant, turn.Mode));
    }

    [Fact]
    public async Task Pointer_off_the_screenshot_is_left_out_and_no_capture_means_no_marks()
    {
        _assistant.Script = (_, _, _) => Events(new ReplyAction(new PointAction(1, 1)), Completed(ReplyStop.EndTurn));

        var result = await Loop.AnswerAsync(new TalkQuestion("hm", RequestSource.Typed, null, new PixelPoint(10, 10)), Begin);

        Assert.Null(_assistant.Requests[0].Screenshot);
        Assert.Empty(_annotations.Shown);
        Assert.Equal(0, result.Marks);
        Assert.Empty(_replies);
        Assert.Equal(MemoryTurnMode.Text, _memory.Turns[0].Mode);
        Assert.Null(_memory.Turns[0].Assistant);

        _assistant.Script = (_, _, _) => Events(Completed(ReplyStop.EndTurn));
        await Loop.AnswerAsync(Question("again", new PixelPoint(5, 5)), Begin);
        Assert.Null(_assistant.Requests[1].Screenshot!.Cursor);
    }

    [Fact]
    public async Task Talking_again_cuts_the_answer_off_and_waits_for_it_before_asking()
    {
        var firstWordSaid = new TaskCompletionSource();
        var active = 0;
        var overlapped = false;
        _assistant.Script = (_, request, token) => Slow(request, token);

        async IAsyncEnumerable<ReplyEvent> Slow(ClaudeRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            overlapped |= Interlocked.Increment(ref active) > 1;
            try
            {
                yield return new ReplyText($"About {request.Text}, ");
                if (request.Text == "first")
                {
                    firstWordSaid.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }

                yield return Completed(ReplyStop.EndTurn);
            }
            finally
            {
                // The backend stores the interrupted turn here; give it time to matter.
                await Task.Delay(30, CancellationToken.None);
                Interlocked.Decrement(ref active);
            }
        }

        var first = Loop.AnswerAsync(Question("first"), Begin);
        await firstWordSaid.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Loop.Begin();
        var second = Loop.AnswerAsync(Question("second"), Begin);

        var cut = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TalkOutcome.Cancelled, cut.Outcome);
        Assert.Equal("About first, ", cut.SpokenText);
        Assert.True(_replies[0].Interrupted);

        var answered = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TalkOutcome.Answered, answered.Outcome);
        Assert.False(overlapped);

        // Both exchanges are remembered, the cut one with what was said before the cut.
        Assert.Equal(["first", "second"], _memory.Turns.Select(t => t.User));
        Assert.Equal(1, _annotations.Dismissals);
    }

    [Fact]
    public async Task Backend_failure_is_reported_without_opening_a_reply()
    {
        _assistant.Script = (_, _, _) => Throws(new ClaudeBackendException(ClaudeFailure.NotConfigured, "Clicky needs an Anthropic API key."));

        var result = await Loop.AnswerAsync(Question("hi"), Begin);

        Assert.Equal(TalkOutcome.Failed, result.Outcome);
        Assert.Equal(ClaudeFailure.NotConfigured, result.Failure);
        Assert.Equal("Clicky needs an Anthropic API key.", result.Problem);
        Assert.Empty(_replies);
        Assert.Empty(_memory.Turns);
    }

    [Fact]
    public async Task Refusal_without_words_still_says_something()
    {
        _assistant.Script = (_, _, _) => Events(Completed(ReplyStop.Refused));

        var result = await Loop.AnswerAsync(Question("hm"), Begin);

        Assert.Equal(TalkLoop.RefusalText, result.SpokenText);
        Assert.Equal(TalkLoop.RefusalText, _replies[0].Text);
    }

    [Fact]
    public async Task Agent_continues_in_the_same_reply_with_its_own_screenshots()
    {
        var stepShot = new RequestScreenshot([7], new ScreenshotGeometry(new PixelRect(0, 0, 1920, 1080), 1920, 1080));
        _agent = new FakeAgent((conversation, completed, screenshot) =>
        {
            Assert.Same(_assistant.Conversation, conversation);
            Assert.Same(Geometry, screenshot!.Geometry);
            return Events(
                new ReplyScreenshotTaken(stepShot),
                new ReplyText(" Done."),
                new ReplyAction(new HighlightAction(10, 10, 50, 20)),
                Completed(ReplyStop.EndTurn));
        });
        _assistant.Script = (_, _, _) => Events(new ReplyText("Opening it."), new ReplyAction(new ClickAction(5, 5)), Completed(ReplyStop.Actions, new ClickAction(5, 5)));

        var result = await Loop.AnswerAsync(Question("open settings"), Begin);

        Assert.Equal(1, _agent.Continued);
        Assert.Equal("Opening it. Done.", Assert.Single(_replies).Text);
        Assert.Same(stepShot.Geometry, Assert.Single(_annotations.Shown).Geometry);
        Assert.Equal(ReplyStop.EndTurn, result.Completed!.Stop);

        // The agent records its own turn; this one has the first answer only.
        Assert.Equal("Opening it.", Assert.Single(_memory.Turns).Assistant);
    }

    [Fact]
    public async Task Agent_is_skipped_when_it_does_not_want_to_act()
    {
        _agent = new FakeAgent((_, _, _) => throw new InvalidOperationException("should not run")) { Wants = false };
        _assistant.Script = (_, _, _) => Events(new ReplyText("Sure."), Completed(ReplyStop.EndTurn));

        var result = await Loop.AnswerAsync(Question("hi"), Begin);

        Assert.Equal(TalkOutcome.Answered, result.Outcome);
        Assert.Equal(0, _agent.Continued);
    }

    [Fact]
    public async Task Feedback_updates_memory_for_the_next_answer_and_a_dropped_conversation_gets_its_memory_pass()
    {
        _memory.Prompt = "<memory>v1</memory>";
        _assistant.Script = (_, _, _) => Events(new ReplyText("Ok."), Completed(ReplyStop.EndTurn));
        await Loop.AnswerAsync(Question("you talk too much"), Begin);
        var conversation = _assistant.Conversation!;
        var generation = conversation.Generation;

        _memory.Prompt = "<memory>v2: keep it short</memory>";
        await _memory.RaiseUpdatedAsync(fromFeedback: true);
        await WaitUntil(() => conversation.Memory == "<memory>v2: keep it short</memory>");
        Assert.Equal(generation + 1, conversation.Generation);

        // An idle pass (not feedback) leaves the running conversation's prompt alone.
        _memory.Prompt = "<memory>v3</memory>";
        await _memory.RaiseUpdatedAsync(fromFeedback: false);
        Assert.Equal("<memory>v2: keep it short</memory>", conversation.Memory);

        Loop.NewConversation();
        await WaitUntil(() => _memory.Ended == 1);
        Assert.Null(_assistant.Conversation);
    }

    [Fact]
    public void Conversation_memory_update_bumps_generation_only_on_change()
    {
        var conversation = new Conversation(memory: " a ");
        Assert.Equal("a", conversation.Memory);
        Assert.False(conversation.UpdateMemory("a"));
        Assert.True(conversation.UpdateMemory("b"));
        Assert.Equal(1, conversation.Generation);
        Assert.True(conversation.UpdateMemory("   "));
        Assert.Null(conversation.Memory);
    }

    private static TalkQuestion Question(string text, PixelPoint? cursor = null) => new(text, RequestSource.Voice, Capture, cursor);

    private static ReplyCompleted Completed(ReplyStop stop, params ClickyAction[] actions) =>
        new(stop, actions, ReplyUsage.None, null, TimeSpan.Zero, "model");

#pragma warning disable CS1998 // Async iterators without awaits are the simplest scripted streams.
    private static async IAsyncEnumerable<ReplyEvent> Events(params ReplyEvent[] events)
    {
        foreach (var replyEvent in events)
        {
            yield return replyEvent;
        }
    }

    private static async IAsyncEnumerable<ReplyEvent> Throws(Exception exception)
    {
        if (exception is not null)
        {
            throw exception;
        }

        yield break;
    }
#pragma warning restore CS1998

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private FakeReply Begin()
    {
        var reply = new FakeReply();
        _replies.Add(reply);
        return reply;
    }

    private sealed class FakeReply : ITalkReply
    {
        private readonly System.Text.StringBuilder _text = new();

        public string Text => _text.ToString();

        public bool Completed { get; private set; }

        public bool Interrupted { get; private set; }

        public void Append(string text) => _text.Append(text);

        public void Complete() => Completed = true;

        public bool Interrupt()
        {
            Interrupted = true;
            return true;
        }
    }

    private sealed class FakeAssistant : IClaudeAssistant
    {
        public event EventHandler<Conversation>? ConversationEnded;

        public Func<Conversation, ClaudeRequest, CancellationToken, IAsyncEnumerable<ReplyEvent>> Script { get; set; } = (_, _, _) => Events();

        public Conversation? Conversation { get; private set; }

        public List<string?> Prepared { get; } = [];

        public List<ClaudeRequest> Requests { get; } = [];

        public Conversation CurrentConversation(string? memory = null) => Conversation ??= new Conversation(memory: memory);

        public void NewConversation()
        {
            var ended = Conversation;
            Conversation = null;
            if (ended is not null)
            {
                ConversationEnded?.Invoke(this, ended);
            }
        }

        public void Prepare(string? memory = null)
        {
            lock (Prepared)
            {
                Prepared.Add(memory);
            }
        }

        public IAsyncEnumerable<ReplyEvent> ReplyAsync(Conversation conversation, ClaudeRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Script(conversation, request, cancellationToken);
        }
    }

    private sealed class FakeMemory : IMemoryService
    {
        public event EventHandler<MemoryUpdatedEventArgs>? Updated;

        public string Prompt { get; set; } = string.Empty;

        public List<MemoryTurn> Turns { get; } = [];

        public int Ended { get; private set; }

        public Task<string> GetPromptAsync(CancellationToken cancellationToken = default) => Task.FromResult(Prompt);

        public void RecordTurn(MemoryTurn turn)
        {
            lock (Turns)
            {
                Turns.Add(turn);
            }
        }

        public Task EndConversationAsync(CancellationToken cancellationToken = default)
        {
            Ended++;
            return Task.CompletedTask;
        }

        public Task RaiseUpdatedAsync(bool fromFeedback)
        {
            var set = new MemorySet(MemoryDocument.Empty, MemoryDocument.Empty);
            Updated?.Invoke(this, new MemoryUpdatedEventArgs(new MergeResult(set, [], []), fromFeedback));
            return Task.Delay(20);
        }
    }

    private sealed class FakeAnnotations : IAnnotationRenderer
    {
        public List<(ClickyAction Action, ScreenshotGeometry Geometry)> Shown { get; } = [];

        public int Dismissals { get; private set; }

        public IAnnotation? Show(ClickyAction action, ScreenshotGeometry geometry, AnnotationOptions? options = null)
        {
            if (action is not (PointAction or HighlightAction))
            {
                return null;
            }

            Shown.Add((action, geometry));
            return new Mark();
        }

        public void DismissAll() => Dismissals++;

        public void Clear()
        {
        }

        private sealed class Mark : IAnnotation
        {
            public void Dismiss()
            {
            }
        }
    }

    private sealed class FakeAgent(Func<Conversation, ReplyCompleted, RequestScreenshot?, IAsyncEnumerable<ReplyEvent>> steps) : IComputerAgent
    {
        public bool Wants { get; init; } = true;

        public int Continued { get; private set; }

        public bool WantsToAct(ReplyCompleted completed) => Wants;

        public IAsyncEnumerable<ReplyEvent> ContinueAsync(Conversation conversation, ReplyCompleted completed, RequestScreenshot? screenshot, CancellationToken cancellationToken)
        {
            Continued++;
            return steps(conversation, completed, screenshot);
        }
    }
}
