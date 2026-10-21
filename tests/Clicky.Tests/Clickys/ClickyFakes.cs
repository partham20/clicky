using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Clicky.Core.Actions;
using Clicky.Core.Assistant;
using Clicky.Core.Clickys;

namespace Clicky.Tests.Clickys;

/// <summary>Holds a fake model or actor mid-flight until the test releases it.</summary>
internal sealed class Gate
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the fake arrives at the gate.</summary>
    public Task Reached => _reached.Task;

    /// <summary>The fake saw its cancellation token fire while held here.</summary>
    public bool Cancelled { get; private set; }

    public void Open() => _release.TrySetResult();

    public async Task PassAsync(CancellationToken cancellationToken)
    {
        _reached.TrySetResult();
        try
        {
            await _release.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Cancelled = true;
            throw;
        }
    }
}

/// <summary>Builds the reply events a backend would stream.</summary>
internal static class Replies
{
    public const string Model = "claude-test-1";

    public static ReplyStarted Started() => new(ClaudeBackendKind.Api, Model, ModelTier.Deep, "test");

    public static ReplyCompleted Completed(ReplyStop stop = ReplyStop.EndTurn, ReplyUsage? usage = null, params ClickyAction[] actions) =>
        new(stop, actions, usage ?? new ReplyUsage(10, 5, 0, 0), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(20), Model);

    /// <summary>A whole one-step reply: started, the text, completed.</summary>
    public static object[] Text(string text, ReplyUsage? usage = null, params ClickyAction[] actions) =>
        [Started(), new ReplyText(text), Completed(ReplyStop.EndTurn, usage, [new SayAction(text), .. actions])];
}

/// <summary>
/// A scripted <see cref="IClickyModel"/>. Each call takes the next script (or <see cref="Default"/>):
/// a list of <see cref="ReplyEvent"/>s to yield, <see cref="Gate"/>s to wait at and
/// <see cref="Exception"/>s to throw. Records every conversation and request it was given.
/// </summary>
internal sealed class FakeClickyModel : IClickyModel
{
    private readonly ConcurrentQueue<object[]> _scripts = new();
    private readonly List<(Conversation Conversation, ClaudeRequest Request)> _calls = [];

    public object[] Default { get; set; } = Replies.Text("Hello from the fake.");

    public IReadOnlyList<(Conversation Conversation, ClaudeRequest Request)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public FakeClickyModel Then(params object[] script)
    {
        _scripts.Enqueue(script);
        return this;
    }

    public async IAsyncEnumerable<ReplyEvent> ReplyAsync(Conversation conversation, ClaudeRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (_calls)
        {
            _calls.Add((conversation, request));
        }

        var script = _scripts.TryDequeue(out var next) ? next : Default;
        foreach (var item in script)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (item)
            {
                case Gate gate:
                    await gate.PassAsync(cancellationToken);
                    break;
                case Exception error:
                    throw error;
                case ReplyEvent replyEvent:
                    await Task.Yield();
                    yield return replyEvent;
                    break;
                default:
                    throw new InvalidOperationException($"Can't script a {item.GetType().Name}.");
            }
        }
    }
}

/// <summary>One step of the fake actor: what it says and does, optionally held at a gate before it starts.</summary>
internal sealed record ActorStep(string Text, ClickyAction[]? Actions = null, Gate? Before = null, ReplyStop Stop = ReplyStop.Actions);

/// <summary>A scripted <see cref="IClickyActor"/>: yields each step's text and completion in turn.</summary>
internal sealed class FakeClickyActor(params ActorStep[] steps) : IClickyActor
{
    private int _stepsStarted;

    public bool Wants { get; set; } = true;

    /// <summary>Steps the service has pulled from the actor (started), so far.</summary>
    public int StepsStarted => Volatile.Read(ref _stepsStarted);

    public int Calls { get; private set; }

    public ClickyActorOptions? Options { get; private set; }

    public RequestScreenshot? Screenshot { get; private set; }

    public ReplyCompleted? First { get; private set; }

    /// <summary>Decides per reply when set, like the real actor; otherwise <see cref="Wants"/>.</summary>
    public Func<ReplyCompleted, bool>? WantsFor { get; set; }

    public bool WantsToAct(ReplyCompleted completed) => WantsFor?.Invoke(completed) ?? Wants;

    public async IAsyncEnumerable<ReplyEvent> ContinueAsync(
        Conversation conversation,
        ReplyCompleted completed,
        RequestScreenshot? screenshot,
        ClickyActorOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Calls++;
        Options = options;
        Screenshot = screenshot;
        First = completed;
        foreach (var step in steps)
        {
            Interlocked.Increment(ref _stepsStarted);
            if (step.Before is { } gate)
            {
                await gate.PassAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ReplyText(step.Text);
            foreach (var action in step.Actions ?? [])
            {
                yield return new ReplyAction(action);
            }

            yield return Replies.Completed(step.Stop, null, [new SayAction(step.Text), .. step.Actions ?? []]);
        }
    }
}
