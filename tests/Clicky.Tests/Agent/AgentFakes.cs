using System.Runtime.CompilerServices;
using System.Text.Json;
using Clicky.Core.Actions;
using Clicky.Core.Agent;
using Clicky.Core.Assistant;
using Clicky.Core.Capture;
using Clicky.Core.Connectors;
using Clicky.Core.Geometry;
using Clicky.Core.Memory;

namespace Clicky.Tests.Agent;

internal static class AgentTestData
{
    public static ScreenshotGeometry Geometry { get; } = new(new PixelRect(0, 0, 1000, 800), 1000, 800);

    public static RequestScreenshot Shot { get; } = new([1], Geometry);

    public static ReplyCompleted Reply(params ClickyAction[] actions) =>
        new(actions.Any(a => a.NeedsResult) ? ReplyStop.Actions : ReplyStop.EndTurn, actions, new ReplyUsage(0, 0, 0, 0), null, TimeSpan.Zero, "m");

    public static ReplyCompleted Calls(params (string Id, string Name, ClickyAction? Action)[] calls) =>
        new(
            ReplyStop.Actions,
            [.. calls.Where(c => c.Action is not null).Select(c => c.Action!)],
            new ReplyUsage(0, 0, 0, 0),
            null,
            TimeSpan.Zero,
            "m",
            ToolCalls: [.. calls.Select(c => new ReplyToolCall(new ToolCallRecord(c.Id, c.Name, "{}", ToolsetName: c.Action is null or ClickAction or TypeAction or KeyAction or ScrollAction ? ComputerToolset.ToolsetName : null), c.Action))]);

    public static Conversation Conversation(string task = "rename the file to notes")
    {
        var conversation = new Conversation();
        conversation.Append(new ConversationTurn($"[Screenshot: 1000 x 800 pixels.]\nThe user said (speech-to-text): {task}", null, "On it.", [], DateTimeOffset.UnixEpoch), new HistoryLimits());
        return conversation;
    }

    public static ConnectorTool Tool(string name, bool readOnly, bool destructive = false) =>
        new(name, "d", JsonDocument.Parse("{}").RootElement, "github", "GitHub", "acct", "partha", name.Split("__")[^1], null, readOnly, destructive, OpenWorld: false);
}

/// <summary>Answers each request with the next scripted reply; "done" once the script runs out.</summary>
internal sealed class ScriptedAssistant : IClaudeAssistant
{
    private readonly Queue<ReplyCompleted> _replies;

    public ScriptedAssistant(params ReplyCompleted[] replies) => _replies = new Queue<ReplyCompleted>(replies);

    public event EventHandler<Conversation>? ConversationEnded;

    public List<ClaudeRequest> Requests { get; } = [];

    public Action<ClaudeRequest>? OnRequest { get; set; }

    public Conversation CurrentConversation(string? memory = null) => new();

    public void NewConversation() => ConversationEnded?.Invoke(this, new Conversation());

    public void Prepare(string? memory = null)
    {
    }

    public async IAsyncEnumerable<ReplyEvent> ReplyAsync(Conversation conversation, ClaudeRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        OnRequest?.Invoke(request);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var reply = _replies.Count > 0 ? _replies.Dequeue() : AgentTestData.Reply(new SayAction("All done."), new DoneAction("Renamed it.", true));
        foreach (var say in reply.Actions.OfType<SayAction>())
        {
            yield return new ReplyText(say.Text);
        }

        yield return reply;
    }
}

internal sealed class FakeComputer : IComputerControl
{
    public List<(ClickyAction Action, ControlOptions Options)> Executed { get; } = [];

    public Func<ClickyAction, ControlTarget> Target { get; set; } = a => a is ClickAction c ? new ControlTarget(c.Target ?? "OK", "button", "Notepad", "notepad") : new ControlTarget(null, "edit", "Notepad", "notepad");

    /// <summary>Outcomes in order; Done once they run out.</summary>
    public Queue<ControlOutcome> Outcomes { get; } = new();

    public Action<ClickyAction>? OnExecute { get; set; }

    public Task<ControlTarget> InspectAsync(ClickyAction action, ScreenshotGeometry? geometry, CancellationToken cancellationToken = default) =>
        Task.FromResult(Target(action));

    public Task<ControlOutcome> ExecuteAsync(ClickyAction action, ScreenshotGeometry? geometry, ControlOptions options, CancellationToken cancellationToken = default)
    {
        Executed.Add((action, options));
        OnExecute?.Invoke(action);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Outcomes.Count > 0 ? Outcomes.Dequeue() : ControlOutcome.Done($"Did {action.Name}."));
    }
}

internal sealed class FakeAgentCapture : ICaptureService
{
    public int Captures { get; private set; }

    public Task<CaptureResult> CaptureAsync(CaptureTarget target, CancellationToken cancellationToken = default)
    {
        Captures++;
        return Task.FromResult(new CaptureResult([2], AgentTestData.Geometry, null, TimeSpan.Zero));
    }

    public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FakeCard : IAgentCard
{
    private readonly CancellationTokenSource _stop = new();

    public CancellationToken StopRequested => _stop.Token;

    public Queue<PermissionAnswer> Answers { get; } = new();

    public List<PermissionPrompt> Prompts { get; } = [];

    public List<string> Countdowns { get; } = [];

    public bool CountdownGoesAhead { get; set; } = true;

    public List<string> Steps { get; } = [];

    public List<PixelPoint> Avoided { get; } = [];

    public (string Message, bool Success)? Finished { get; private set; }

    public bool Disposed { get; private set; }

    public void Stop() => _stop.Cancel();

    public Task<PermissionAnswer> AskAsync(PermissionPrompt prompt, CancellationToken cancellationToken)
    {
        Prompts.Add(prompt);
        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : PermissionAnswer.AllowOnce);
    }

    public void ShowStep(int number, string text) => Steps.Add($"{number}: {text}");

    public Task<bool> CountdownAsync(string upcoming, TimeSpan duration, CancellationToken cancellationToken)
    {
        Countdowns.Add(upcoming);
        return Task.FromResult(CountdownGoesAhead);
    }

    public void Avoid(PixelPoint point) => Avoided.Add(point);

    public void Finish(string message, bool success) => Finished = (message, success);

    public void Dispose()
    {
        Disposed = true;
        _stop.Dispose();
    }
}

internal sealed class FakePresenter : IAgentPresenter
{
    public List<FakeCard> Cards { get; } = [];

    public Func<FakeCard> NewCard { get; set; } = () => new FakeCard();

    public FakeCard Last => Cards[^1];

    public IAgentCard Begin(string task, PixelRect? near)
    {
        var card = NewCard();
        Cards.Add(card);
        Tasks.Add(task);
        return card;
    }

    public List<string> Tasks { get; } = [];
}

internal sealed class FakeConnectorTools(params ConnectorTool[] tools) : IConnectorTools
{
    public event EventHandler? ToolsChanged;

    public IReadOnlyList<ConnectorTool> Tools { get; } = tools;

    public List<string> Called { get; } = [];

    public string Output { get; set; } = "3 open issues";

    public Task<IReadOnlyList<ConnectorTool>> GetToolsAsync(TimeSpan maxWait, CancellationToken cancellationToken = default) => Task.FromResult(Tools);

    public bool IsConnectorTool(string toolName) => Tools.Any(t => t.Name == toolName);

    public Task<ConnectorToolResult> CallAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Called.Add(toolName);
        ToolsChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new ConnectorToolResult(false, [new ConnectorContent.Text(Output)]));
    }
}

/// <summary>Tools the run's caller answers (a Clicky's routine tools): create_routine calls and routine tags.</summary>
internal sealed class FakeRunTools(params ConnectorTool[] offered) : IRunTools
{
    public IReadOnlyList<ConnectorTool> Offered { get; } = offered;

    public List<(ClickyAction Action, string? ToolCallId)> Called { get; } = [];

    public bool Answers(ClickyAction action) =>
        action is RoutineAction || (action is UseToolAction use && Offered.Any(t => t.Name == use.Tool));

    public Task<ToolOutcome> CallAsync(ClickyAction action, string? toolCallId, CancellationToken cancellationToken)
    {
        Called.Add((action, toolCallId));
        return Task.FromResult(new ToolOutcome(toolCallId, action is UseToolAction use ? use.Tool : action.Name, "The user saved the routine.", IsError: false));
    }
}

internal sealed class RecordingMemory : IMemoryService
{
    public event EventHandler<MemoryUpdatedEventArgs>? Updated;

    public List<MemoryTurn> Turns { get; } = [];

    public Task<string> GetPromptAsync(CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

    public void RecordTurn(MemoryTurn turn) => Turns.Add(turn);

    public Task EndConversationAsync(CancellationToken cancellationToken = default)
    {
        Updated?.Invoke(this, null!);
        return Task.CompletedTask;
    }
}

/// <summary>Every delay and timer fires at once, so agent tests don't wait out the settle delay.</summary>
internal sealed class InstantTime : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Instant(callback, state, dueTime);

    private sealed class Instant : ITimer
    {
        public Instant(TimerCallback callback, object? state, TimeSpan dueTime)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
