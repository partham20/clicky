using System.Collections.Concurrent;
using Clicky.Core.Actions;
using Clicky.Core.Annotations;
using Clicky.Core.Assistant;
using Clicky.Core.Geometry;
using Clicky.Core.Skills;
using Clicky.Core.Walkthroughs;

namespace Clicky.Tests.Walkthroughs;

internal sealed class FakeStepWatcher : IStepWatcher
{
    private Action<StepObservation>? _observer;

    public int Starts { get; private set; }

    public int Stops { get; private set; }

    public bool IsWatching => _observer is not null;

    public IDisposable Start(Action<StepObservation> observer)
    {
        Starts++;
        _observer = observer;
        return new Stopper(this);
    }

    public void Report(StepObservation observation) =>
        (_observer ?? throw new InvalidOperationException("Not watching.")).Invoke(observation);

    private sealed class Stopper(FakeStepWatcher owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner.Stops++;
                owner._observer = null;
            }
        }
    }
}

internal sealed class FakeRenderer : IAnnotationRenderer
{
    public ConcurrentQueue<(ClickyAction Action, AnnotationOptions? Options, FakeMark Mark)> Shown { get; } = new();

    public IAnnotation? Show(ClickyAction action, ScreenshotGeometry geometry, AnnotationOptions? options = null)
    {
        if (!AnnotationPlanner.Draws(action))
        {
            return null;
        }

        var mark = new FakeMark();
        Shown.Enqueue((action, options, mark));
        return mark;
    }

    public void DismissAll()
    {
    }

    public void Clear()
    {
    }

    public FakeMark LastMark => Shown.Last().Mark;
}

internal sealed class FakeMark : IAnnotation
{
    public bool Dismissed { get; private set; }

    public void Dismiss() => Dismissed = true;
}

internal sealed class FakeForeground(string process = "EXCEL.EXE", string title = "Book1 - Excel") : IForegroundApp
{
    public ForegroundApp? App { get; set; } = new(process, title);

    public ForegroundApp? Current() => App;
}

/// <summary>Records every turn and lets the test answer it.</summary>
internal sealed class FakeTurns : IWalkthroughTurns
{
    private readonly ConcurrentQueue<PendingTurn> _turns = new();

    public IReadOnlyList<PendingTurn> All => [.. _turns];

    public PendingTurn Last => _turns.Last();

    public Task RunAsync(WalkthroughTurn turn, IWalkthroughs walkthroughs, CancellationToken cancellationToken)
    {
        var pending = new PendingTurn(turn, walkthroughs, cancellationToken);
        _turns.Enqueue(pending);
        return pending.Completion;
    }
}

internal sealed class PendingTurn
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PendingTurn(WalkthroughTurn turn, IWalkthroughs scope, CancellationToken token)
    {
        Turn = turn;
        Scope = scope;
        Token = token;
        token.Register(() => _done.TrySetCanceled(token));
    }

    public WalkthroughTurn Turn { get; }

    public IWalkthroughs Scope { get; }

    public CancellationToken Token { get; }

    public Task Completion => _done.Task;

    /// <summary>Replies through the turn's scope, then ends the turn.</summary>
    public void Reply(ScreenshotGeometry geometry, params ClickyAction[] actions)
    {
        WalkthroughTestKit.Send(Scope, geometry, actions);
        _done.TrySetResult();
    }

    public void Fail(Exception exception) => _done.TrySetException(exception);
}

internal static class WalkthroughTestKit
{
    /// <summary>A 1920x1080 monitor at 100%, captured without downscaling, so screenshot px = physical px.</summary>
    public static readonly MonitorInfo Screen = new(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true);

    public static readonly ScreenshotGeometry Shot = new(Screen.Bounds, 1920, 1080);

    public static StepAction Step(int index, double? x = 500, double? y = 300, int? total = 4, string? instruction = null) =>
        new(index, instruction ?? $"Do thing {index}", total, x, y);

    /// <summary>Sends one whole reply (started, actions, completed) the way a talk loop would.</summary>
    public static void Send(IWalkthroughs walkthroughs, ScreenshotGeometry? geometry, params ClickyAction[] actions)
    {
        walkthroughs.Observe(new ReplyStarted(ClaudeBackendKind.Api, "test", ModelTier.Deep, "test"), geometry);
        foreach (var action in actions)
        {
            walkthroughs.Observe(action is SayAction say ? new ReplyText(say.Text) : new ReplyAction(action), geometry);
        }

        walkthroughs.Observe(new ReplyCompleted(ReplyStop.EndTurn, actions, ReplyUsage.None, null, TimeSpan.Zero, "test"), geometry);
    }
}
