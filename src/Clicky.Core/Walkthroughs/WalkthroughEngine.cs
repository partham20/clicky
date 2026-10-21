using System.Globalization;
using System.Threading.Channels;
using Clicky.Core.Actions;
using Clicky.Core.Annotations;
using Clicky.Core.Assistant;
using Clicky.Core.Buddy;
using Clicky.Core.Capture;
using Clicky.Core.Geometry;
using Clicky.Core.Skills;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Walkthroughs;

/// <summary>
/// The walkthrough state machine (see <see cref="IWalkthroughs"/>). One walkthrough at a time:
/// <c>Waiting</c> for the user to do the step on screen, <c>Settling</c> while the app reacts,
/// <c>Thinking</c> while Claude looks at the new screen, and back to <c>Waiting</c> on the next step,
/// until Claude says done, the user stops, or the step limit is reached.
/// </summary>
/// <remarks>
/// <para>Every input (reply events, what the watcher sees, card buttons, timers) is queued and handled
/// one at a time on a loop of its own, so the state needs no locks and nothing here blocks a caller.</para>
/// <para>When the user starts talking, a request Clicky made on its own is cancelled and made again once
/// the user's question has been answered and spoken, unless that answer brought a step of its own.</para>
/// </remarks>
public sealed class WalkthroughEngine : IWalkthroughs, IDisposable
{
    private readonly IStepWatcher _watcher;
    private readonly IAnnotationRenderer _renderer;
    private readonly IWalkthroughTurns _turns;
    private readonly IBuddyPresence _presence;
    private readonly IForegroundApp _foreground;
    private readonly Func<IReadOnlyList<MonitorInfo>> _monitors;
    private readonly ILogger<WalkthroughEngine> _logger;
    private readonly WalkthroughOptions _options;
    private readonly TimeProvider _time;
    private readonly Channel<Action> _inbox = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _loop;

    // Loop only.
    private Session? _session;
    private ReplyTrack? _userReply;
    private bool _userTalking;
    private int _nextTurnId;

    private WalkthroughSnapshot? _current;
    private bool _disposed;

    /// <param name="monitors">The monitors as they are now, to size the click tolerance.</param>
    public WalkthroughEngine(
        IStepWatcher watcher,
        IAnnotationRenderer renderer,
        IWalkthroughTurns turns,
        IBuddyPresence presence,
        IForegroundApp foreground,
        Func<IReadOnlyList<MonitorInfo>> monitors,
        ILogger<WalkthroughEngine> logger,
        WalkthroughOptions? options = null,
        TimeProvider? time = null)
    {
        _watcher = watcher;
        _renderer = renderer;
        _turns = turns;
        _presence = presence;
        _foreground = foreground;
        _monitors = monitors;
        _logger = logger;
        _options = options ?? WalkthroughOptions.Default;
        _time = time ?? TimeProvider.System;
        _presence.StateChanged += OnPresenceChanged;
        _loop = Task.Run(RunLoopAsync);
    }

    public event EventHandler<WalkthroughChangedEventArgs>? Changed;

    public WalkthroughSnapshot? Current => Volatile.Read(ref _current);

    public void Observe(ReplyEvent replyEvent, ScreenshotGeometry? geometry)
    {
        ArgumentNullException.ThrowIfNull(replyEvent);
        Post(() => OnReply(replyEvent, geometry, turnId: null));
    }

    public void MarkDone() => Post(() => Advance(WalkthroughStepStatus.Done));

    public void Skip() => Post(() => Advance(WalkthroughStepStatus.Skipped));

    public void Back() => Post(GoBack);

    public void Retry() => Post(() =>
    {
        if (_session is { Phase: WalkthroughPhase.Waiting, Pending: not null } session)
        {
            StartTurn(session);
        }
    });

    public void Cancel() => Post(() =>
    {
        if (_session is { } session)
        {
            _logger.LogInformation("Walkthrough {Id} stopped by the user", session.Id);
            Finish(session, WalkthroughEnd.Cancelled);
        }
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _presence.StateChanged -= OnPresenceChanged;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (_session is { } session)
            {
                Finish(session, WalkthroughEnd.Cancelled);
            }

            finished.TrySetResult();
        });
        _inbox.Writer.TryComplete();
        finished.Task.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>Completes once everything queued so far has been handled (tests).</summary>
    internal Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => done.TrySetResult());
        return done.Task;
    }

    private void Post(Action work) => _inbox.Writer.TryWrite(work);

    private async Task RunLoopAsync()
    {
        await foreach (var work in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Walkthrough step failed");
            }
        }
    }

    private void OnPresenceChanged(object? sender, BuddyStateChangedEventArgs e)
    {
        if (e.Current == BuddyState.Listening)
        {
            Post(OnUserTalking);
        }
        else if (e.Current == BuddyState.Idle)
        {
            Post(OnUserQuiet);
        }
    }

    // ---- Replies ----

    private void OnReply(ReplyEvent replyEvent, ScreenshotGeometry? geometry, int? turnId)
    {
        ReplyTrack? track;
        if (turnId is { } id)
        {
            // A request of ours; drop what a cancelled or replaced one still sends.
            if (_session is not { } owner || owner.TurnId != id || owner.Turn is null)
            {
                return;
            }

            track = owner.TurnReply ??= new ReplyTrack(ours: true);
        }
        else
        {
            if (replyEvent is ReplyStarted)
            {
                _userReply = new ReplyTrack(ours: false);
            }

            track = _userReply ??= new ReplyTrack(ours: false);
        }

        switch (replyEvent)
        {
            case ReplyAction { Action: StepAction step }:
                if (track.StepSeen)
                {
                    _logger.LogDebug("Walkthrough: ignoring step {Index}; one step per reply", step.Index);
                    return;
                }

                track.StepSeen = true;
                if (geometry is null)
                {
                    _logger.LogWarning("Walkthrough: step {Index} came without a screenshot; not shown", step.Index);
                    return;
                }

                ShowStep(step, geometry, track.Ours);
                break;

            case ReplyAction { Action: DoneAction }:
                track.DoneSeen = true;
                break;

            case ReplyCompleted completed:
                OnReplyCompleted(completed, geometry, track);
                break;
        }
    }

    private void OnReplyCompleted(ReplyCompleted completed, ScreenshotGeometry? geometry, ReplyTrack track)
    {
        if (!track.Ours)
        {
            _userReply = null;
        }

        // A talk loop that only passes the completed reply still gets its step shown.
        if (!track.StepSeen && geometry is not null && completed.Actions.OfType<StepAction>().FirstOrDefault() is { } step)
        {
            track.StepSeen = true;
            ShowStep(step, geometry, track.Ours);
        }

        if (_session is not { } session || track.StepSeen)
        {
            return;
        }

        if (track.DoneSeen || completed.Actions.OfType<DoneAction>().Any() || track.Ours)
        {
            // Claude said done, or answered our "what next?" without a step: it's over either way.
            Finish(session, session.LimitHit ? WalkthroughEnd.LimitReached : WalkthroughEnd.Completed);
            return;
        }

        // The user asked something in between and got an answer without a step: the walkthrough goes on,
        // now if the answer was shown rather than spoken, else once it has been said (OnUserQuiet).
        if (session is { Pending: not null, Phase: WalkthroughPhase.Settling, Settle: null, Turn: null } && !_userTalking)
        {
            StartSettle(session);
        }
    }

    // ---- Steps ----

    private void ShowStep(StepAction step, ScreenshotGeometry geometry, bool fromOurTurn)
    {
        var session = _session;
        if (session is not null && !fromOurTurn && step.Index == 1 && session.Steps.Any(s => s.Status != WalkthroughStepStatus.Current))
        {
            _logger.LogInformation("Walkthrough {Id} replaced by a new one", session.Id);
            Finish(session, WalkthroughEnd.Replaced);
            session = null;
        }

        if (step.Index > _options.MaxSteps)
        {
            _logger.LogInformation("Walkthrough: step {Index} is past the limit of {Max}", step.Index, _options.MaxSteps);
            if (session is not null)
            {
                Finish(session, WalkthroughEnd.LimitReached);
            }

            return;
        }

        if (session is null)
        {
            session = new Session(Guid.NewGuid());
            _session = session;
            StartWatching(session);
            _logger.LogInformation("Walkthrough {Id} started: {Total} steps planned", session.Id, step.Total?.ToString(CultureInfo.InvariantCulture) ?? "?");
        }

        CancelSettle(session);
        session.Marker?.Dismiss();

        // A re-plan or a step back renumbers from here; anything later is gone.
        session.Steps.RemoveAll(s => s.Action.Index >= step.Index);
        foreach (var earlier in session.Steps.Where(s => s.Status == WalkthroughStepStatus.Current))
        {
            earlier.Status = WalkthroughStepStatus.Done;
        }

        var target = TargetFor(step, geometry);
        session.Steps.Add(new Step(step, geometry, target));
        session.Total = step.Total ?? session.Total;
        if (session.ReplanIndex != step.Index)
        {
            session.ReplanIndex = step.Index;
            session.Replans = 0;
        }

        session.Strays = 0;
        session.Pending = null;
        session.Problem = null;
        session.Phase = WalkthroughPhase.Waiting;
        session.Marker = _renderer.Show(step, geometry, new AnnotationOptions(Hold: Timeout.InfiniteTimeSpan));
        ArmIdleTimeout(session);
        _logger.LogInformation(
            "Walkthrough step {Index}: {Instruction} ({Where})",
            step.Index,
            step.Instruction,
            target is null ? "no spot on screen" : $"at {target.Point.X},{target.Point.Y} ±{target.Tolerance}px in {target.App ?? "?"}");
        Publish(session);
    }

    private StepTarget? TargetFor(StepAction step, ScreenshotGeometry geometry)
    {
        if (step is not { X: { } x, Y: { } y })
        {
            return null;
        }

        var point = CoordinateMapper.ToPhysical(new ScreenshotPoint(x, y), geometry);
        IReadOnlyList<MonitorInfo> monitors;
        try
        {
            monitors = _monitors();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not list monitors");
            monitors = [];
        }

        var monitor = CoordinateMapper.NearestMonitor(monitors, point);
        return new StepTarget(
            point,
            StepJudge.Tolerance(monitor?.Scale ?? 1, geometry, _options),
            monitor?.Bounds ?? geometry.Source,
            _foreground.Current()?.App);
    }

    private void StartWatching(Session session)
    {
        try
        {
            session.Watch = _watcher.Start(observation => Post(() => OnObservation(session, observation)));
        }
        catch (Exception ex)
        {
            // Still usable: the card's Done button moves it on.
            _logger.LogWarning(ex, "Walkthrough: could not watch for clicks; steps advance only from the card");
        }
    }

    private void OnObservation(Session session, StepObservation observation)
    {
        if (!ReferenceEquals(_session, session))
        {
            return;
        }

        if (observation is PointerClicked { OnClicky: false })
        {
            Volatile.Write(ref session.LastInput, _time.GetTimestamp());
        }

        if (session.Phase != WalkthroughPhase.Waiting || session.Steps.Count == 0 || session.Steps[^1] is not { Status: WalkthroughStepStatus.Current } step)
        {
            return;
        }

        switch (StepJudge.Judge(step.Target, observation, _options))
        {
            case StepVerdict.Done:
                _logger.LogInformation("Walkthrough step {Index} done ({Observation})", step.Action.Index, observation);
                step.Status = WalkthroughStepStatus.Done;
                session.Pending = TurnAfter(session, step, WalkthroughTurnReason.StepDone);
                StartSettle(session);
                break;

            case StepVerdict.Stray:
                session.Strays++;
                _logger.LogDebug("Walkthrough step {Index}: stray click {Count}/{Max}", step.Action.Index, session.Strays, _options.StrayClicksBeforeReplan);
                if (session.Strays >= _options.StrayClicksBeforeReplan)
                {
                    OffTrack(session, step, observation);
                }

                break;

            case StepVerdict.OffTrack:
                OffTrack(session, step, observation);
                break;
        }
    }

    private void OffTrack(Session session, Step step, StepObservation observation)
    {
        session.Strays = 0;
        if (session.Replans >= _options.MaxReplansPerStep)
        {
            _logger.LogDebug("Walkthrough step {Index}: off track again, already looked {Count} times; waiting", step.Action.Index, session.Replans);
            return;
        }

        session.Replans++;
        var detail = Describe(observation, step);
        _logger.LogInformation("Walkthrough step {Index}: off track ({Detail}); taking a new look", step.Action.Index, detail);
        session.Pending = TurnAfter(session, step, WalkthroughTurnReason.OffTrack, detail);
        StartSettle(session);
    }

    private static string Describe(StepObservation observation, Step step)
    {
        switch (observation)
        {
            case PointerClicked click:
                var at = CoordinateMapper.ToScreenshot(click.At, step.Geometry);
                return string.Create(CultureInfo.InvariantCulture, $"clicked somewhere else (last at about ({at.X:0}, {at.Y:0}) on the previous screenshot)");
            case ElementEvent { Name: { Length: > 0 } name }:
                return $"pressed \"{name}\" instead";
            case ElementEvent:
                return "pressed a different button";
            case ForegroundChanged change:
                return string.IsNullOrWhiteSpace(change.App.Title) ? $"switched to {change.App.App}" : $"switched to {change.App.App} (\"{change.App.Title}\")";
            default:
                return "did something else";
        }
    }

    private WalkthroughTurn TurnAfter(Session session, Step step, WalkthroughTurnReason reason, string? detail = null)
    {
        if (reason != WalkthroughTurnReason.OffTrack && step.Action.Index >= _options.MaxSteps)
        {
            reason = WalkthroughTurnReason.LimitReached;
        }

        return new WalkthroughTurn(session.Id, reason, WalkthroughPrompts.For(reason, step.Action.Index, step.Action.Instruction, _options.MaxSteps, detail), step.Action.Index);
    }

    private void Advance(WalkthroughStepStatus status)
    {
        if (_session is not { } session)
        {
            return;
        }

        if (session.Phase == WalkthroughPhase.Waiting && session.Steps.Count > 0 && session.Steps[^1] is { Status: WalkthroughStepStatus.Current } step)
        {
            _logger.LogInformation("Walkthrough step {Index} {Status} from the card", step.Action.Index, status == WalkthroughStepStatus.Done ? "done" : "skipped");
            step.Status = status;
            session.Pending = TurnAfter(session, step, status == WalkthroughStepStatus.Done ? WalkthroughTurnReason.StepDone : WalkthroughTurnReason.StepSkipped);
            StartTurn(session);
        }
        else if (session is { Phase: WalkthroughPhase.Waiting, Pending: not null })
        {
            StartTurn(session);
        }
    }

    private void GoBack()
    {
        if (_session is not { } session || session.Phase == WalkthroughPhase.Finished || session.Steps.Count < 2)
        {
            return;
        }

        CancelSettle(session);
        CancelTurn(session);
        session.Steps.RemoveAt(session.Steps.Count - 1);
        var previous = session.Steps[^1];
        previous.Status = WalkthroughStepStatus.Current;
        session.Pending = null;
        session.Problem = null;
        session.Strays = 0;
        session.Phase = WalkthroughPhase.Waiting;
        session.Marker?.Dismiss();
        session.Marker = _renderer.Show(previous.Action, previous.Geometry, new AnnotationOptions(Hold: Timeout.InfiniteTimeSpan));
        ArmIdleTimeout(session);
        _logger.LogInformation("Walkthrough back to step {Index}", previous.Action.Index);
        Publish(session);
    }

    // ---- Settling and requests ----

    /// <param name="first">The first wait; <see cref="WalkthroughOptions.Settle"/> by default.</param>
    private void StartSettle(Session session, TimeSpan? first = null)
    {
        CancelSettle(session);
        session.Phase = WalkthroughPhase.Settling;
        var settle = new CancellationTokenSource();
        session.Settle = settle;
        Publish(session);
        _ = SettleAsync(session, settle, first ?? _options.Settle);
    }

    private async Task SettleAsync(Session session, CancellationTokenSource settle, TimeSpan first)
    {
        try
        {
            // After the first wait, keep waiting while the user is still clicking, up to MaxSettle in all.
            var started = _time.GetTimestamp();
            var longest = first + _options.MaxSettle - _options.Settle;
            await Task.Delay(first, _time, settle.Token).ConfigureAwait(false);
            while (true)
            {
                var sinceInput = _time.GetElapsedTime(Volatile.Read(ref session.LastInput));
                var total = _time.GetElapsedTime(started);
                if (sinceInput >= _options.QuietAfterInput || total >= longest)
                {
                    break;
                }

                var wait = Min(_options.QuietAfterInput - sinceInput, longest - total);
                await Task.Delay(wait, _time, settle.Token).ConfigureAwait(false);
            }

            Post(() =>
            {
                if (ReferenceEquals(_session, session) && ReferenceEquals(session.Settle, settle))
                {
                    session.Settle = null;
                    settle.Dispose();
                    StartTurn(session);
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StartTurn(Session session)
    {
        if (session.Pending is not { } turn)
        {
            return;
        }

        CancelSettle(session);
        if (_userReply is not null || _userTalking)
        {
            // The user is asking, or being answered: go on once that's over (OnUserQuiet, OnReplyCompleted).
            session.Phase = WalkthroughPhase.Settling;
            Publish(session);
            return;
        }

        CancelTurn(session);
        session.Pending = null;
        session.Problem = null;
        session.Phase = WalkthroughPhase.Thinking;
        if (turn.Reason == WalkthroughTurnReason.LimitReached)
        {
            session.LimitHit = true;
        }

        var cancel = new CancellationTokenSource();
        var id = ++_nextTurnId;
        session.Turn = cancel;
        session.TurnId = id;
        session.TurnReply = null;
        Publish(session);
        _logger.LogInformation("Walkthrough {Id}: asking for what comes after step {Index} ({Reason})", session.Id, turn.StepIndex, turn.Reason);
        _ = RunTurnAsync(session, id, turn, cancel);
    }

    private async Task RunTurnAsync(Session session, int id, WalkthroughTurn turn, CancellationTokenSource cancel)
    {
        Exception? failure = null;
        try
        {
            await Task.Yield();
            await _turns.RunAsync(turn, new TurnScope(this, id), cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Post(() => OnTurnEnded(session, id, turn, cancel, failure));
    }

    private void OnTurnEnded(Session session, int id, WalkthroughTurn turn, CancellationTokenSource cancel, Exception? failure)
    {
        var current = ReferenceEquals(_session, session) && session.TurnId == id && ReferenceEquals(session.Turn, cancel);
        var cancelled = cancel.IsCancellationRequested;
        cancel.Dispose();
        if (!current)
        {
            return;
        }

        session.Turn = null;
        session.TurnReply = null;
        if (session.Phase != WalkthroughPhase.Thinking)
        {
            // A step arrived (Waiting) or the user went back.
            return;
        }

        if (cancelled)
        {
            // The user started talking: ask again once they're done.
            _logger.LogInformation("Walkthrough {Id}: request cancelled; asking again after the user's question", session.Id);
            session.Pending = turn;
            session.Phase = WalkthroughPhase.Settling;
            Publish(session);
        }
        else if (failure is not null)
        {
            _logger.LogWarning(failure, "Walkthrough {Id}: could not get the next step", session.Id);
            session.Pending = turn;
            session.Phase = WalkthroughPhase.Waiting;
            session.Problem = failure is ClaudeBackendException or CaptureException ? failure.Message : "Something went wrong getting the next step.";
            Publish(session);
        }
        else
        {
            // The turn ran to the end without a step or a completed reply reaching us.
            Finish(session, session.LimitHit ? WalkthroughEnd.LimitReached : WalkthroughEnd.Completed);
        }
    }

    private void OnUserTalking()
    {
        _userTalking = true;
        if (_session is not { } session)
        {
            return;
        }

        if (session.Turn is { } turn)
        {
            turn.Cancel();
        }

        // A settle in progress waits for the user's question to be answered.
        CancelSettle(session);
    }

    private void OnUserQuiet()
    {
        var wasTalking = _userTalking;
        _userTalking = false;
        if (wasTalking && _session is { Phase: WalkthroughPhase.Settling, Pending: not null, Settle: null, Turn: null } session && _userReply is null)
        {
            // Pressed Talk and said nothing, or the answer has been spoken. Leave a moment for a question
            // that is still being transcribed to start its reply (which defers us again).
            StartSettle(session, _options.AfterQuestion);
        }
    }

    private static void CancelSettle(Session session)
    {
        if (session.Settle is { } settle)
        {
            session.Settle = null;
            settle.Cancel();
            settle.Dispose();
        }
    }

    private static void CancelTurn(Session session)
    {
        // OnTurnEnded disposes it.
        session.Turn?.Cancel();
        session.Turn = null;
        session.TurnReply = null;
    }

    private void ArmIdleTimeout(Session session)
    {
        session.Idle?.Dispose();
        var generation = ++session.IdleGeneration;
        session.Idle = _time.CreateTimer(
            _ => Post(() =>
            {
                if (ReferenceEquals(_session, session) && session.IdleGeneration == generation)
                {
                    _logger.LogInformation("Walkthrough {Id}: no progress for {Minutes} min; ending it", session.Id, _options.IdleTimeout.TotalMinutes);
                    Finish(session, WalkthroughEnd.TimedOut);
                }
            }),
            null,
            _options.IdleTimeout,
            Timeout.InfiniteTimeSpan);
    }

    private void Finish(Session session, WalkthroughEnd end)
    {
        if (!ReferenceEquals(_session, session))
        {
            return;
        }

        CancelSettle(session);
        CancelTurn(session);
        session.Marker?.Dismiss();
        session.Marker = null;
        session.Idle?.Dispose();
        session.Idle = null;
        try
        {
            session.Watch?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping the step watcher failed");
        }

        session.Watch = null;
        if (end == WalkthroughEnd.Completed && session.Steps.Count > 0 && session.Steps[^1].Status == WalkthroughStepStatus.Current)
        {
            session.Steps[^1].Status = WalkthroughStepStatus.Done;
        }

        session.Phase = WalkthroughPhase.Finished;
        session.End = end;
        session.Pending = null;
        _session = null;
        _logger.LogInformation("Walkthrough {Id} finished: {End} after {Steps} step(s)", session.Id, end, session.Steps.Count);
        Publish(session);
    }

    private void Publish(Session session)
    {
        var snapshot = new WalkthroughSnapshot(
            session.Id,
            session.Phase,
            session.Total,
            [.. session.Steps.Select(s => new WalkthroughStepInfo(s.Action.Index, s.Action.Instruction, s.Target is not null, s.Status))],
            session.End,
            session.Problem);
        Volatile.Write(ref _current, snapshot.IsActive ? snapshot : null);
        try
        {
            Changed?.Invoke(this, new WalkthroughChangedEventArgs(snapshot));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Walkthrough change handler failed");
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private sealed class Session(Guid id)
    {
        public long LastInput;

        public Guid Id { get; } = id;

        public List<Step> Steps { get; } = [];

        public int? Total { get; set; }

        public WalkthroughPhase Phase { get; set; } = WalkthroughPhase.Waiting;

        public WalkthroughEnd? End { get; set; }

        public string? Problem { get; set; }

        /// <summary>The request to make once the screen has settled.</summary>
        public WalkthroughTurn? Pending { get; set; }

        public IDisposable? Watch { get; set; }

        public IAnnotation? Marker { get; set; }

        public CancellationTokenSource? Settle { get; set; }

        public CancellationTokenSource? Turn { get; set; }

        public int TurnId { get; set; }

        public ReplyTrack? TurnReply { get; set; }

        public int Strays { get; set; }

        public int Replans { get; set; }

        public int ReplanIndex { get; set; }

        public bool LimitHit { get; set; }

        public ITimer? Idle { get; set; }

        public int IdleGeneration { get; set; }
    }

    private sealed class Step(StepAction action, ScreenshotGeometry geometry, StepTarget? target)
    {
        public StepAction Action { get; } = action;

        public ScreenshotGeometry Geometry { get; } = geometry;

        public StepTarget? Target { get; } = target;

        public WalkthroughStepStatus Status { get; set; } = WalkthroughStepStatus.Current;
    }

    private sealed class ReplyTrack(bool ours)
    {
        public bool Ours { get; } = ours;

        public bool StepSeen { get; set; }

        public bool DoneSeen { get; set; }
    }

    /// <summary>What a turn runner sees as <see cref="IWalkthroughs"/>: its events are tagged as that turn's.</summary>
    private sealed class TurnScope(WalkthroughEngine engine, int turnId) : IWalkthroughs
    {
        public WalkthroughSnapshot? Current => engine.Current;

        public event EventHandler<WalkthroughChangedEventArgs>? Changed
        {
            add => engine.Changed += value;
            remove => engine.Changed -= value;
        }

        public void Observe(ReplyEvent replyEvent, ScreenshotGeometry? geometry)
        {
            ArgumentNullException.ThrowIfNull(replyEvent);
            engine.Post(() => engine.OnReply(replyEvent, geometry, turnId));
        }

        public void MarkDone() => engine.MarkDone();

        public void Skip() => engine.Skip();

        public void Back() => engine.Back();

        public void Retry() => engine.Retry();

        public void Cancel() => engine.Cancel();
    }
}
