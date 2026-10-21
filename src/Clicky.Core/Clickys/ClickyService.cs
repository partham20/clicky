using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using Clicky.Core.Actions;
using Clicky.Core.Assistant;
using Clicky.Core.Capture;
using Clicky.Core.Memory;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Clickys;

/// <summary>
/// Clickys: persistent helpers with their own chat, notes, files and run history (clickys.db). The
/// one API Home, routines (M15) and voice commands use to create, run, pause, resume and stop them.
/// </summary>
/// <remarks>
/// <para>
/// A run is one task: a chat message or a routine's prompt. Each Clicky runs one task at a time
/// (later ones queue), and only one run at a time may use the computer. A run asks Claude with the
/// Clicky's context (<see cref="ClickyPrompt"/>) and earlier chat as history; if the reply acts on
/// the computer, the <see cref="IClickyActor"/> carries on until the task is done or the Clicky's
/// step cap. Pause holds the run at the next step boundary. The reply lands in the chat and counts
/// as unread; nothing is spoken.
/// </para>
/// <para>Thread-safe. Events are raised on background threads.</para>
/// </remarks>
public sealed class ClickyService : IAsyncDisposable
{
    /// <summary>Text of a run step kept in the activity log.</summary>
    private const int MaxStepChars = 600;

    private const string InterruptedError = "Clicky closed while this was running.";

    private const string ToolsAnsweredText = "Those are the results. Answer the user, or carry on with the task.";

    private readonly IClickyModel _model;
    private readonly IClickyActor _actor;
    private readonly ICaptureService? _capture;
    private readonly IMemoryService? _memory;
    private readonly IClickyTools? _tools;
    private readonly ILogger<ClickyService> _logger;
    private readonly TimeProvider _time;
    private readonly Lazy<Task<ClickyStore>> _store;
    private readonly ConcurrentDictionary<string, ActiveRun> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _clickyTurns = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _computer = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    public ClickyService(
        ClickyPaths paths,
        IClickyModel model,
        IClickyActor actor,
        ILogger<ClickyService> logger,
        ICaptureService? capture = null,
        IMemoryService? memory = null,
        TimeProvider? time = null,
        IClickyTools? tools = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _model = model;
        _actor = actor;
        _capture = capture;
        _memory = memory;
        _tools = tools;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        DatabasePath = Path.Combine(paths.Root, ClickyDatabase.FileName);
        Workspace = ClickyWorkspace.For(paths);
        _store = new(OpenAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>A Clicky, its chat, a run or its activity changed.</summary>
    public event EventHandler<ClickyChangedEventArgs>? Changed;

    /// <summary>A run reached an end state (completed, failed, stopped, interrupted).</summary>
    public event EventHandler<ClickyRunFinishedEventArgs>? RunFinished;

    public string DatabasePath { get; }

    public ClickyWorkspace Workspace { get; }

    /// <summary>Runs queued, running or paused right now.</summary>
    public IReadOnlyList<ClickyRun> ActiveRuns => [.. _active.Values.Select(a => a.Run).OrderBy(r => r.QueuedAt)];

    /// <summary>
    /// Opens clickys.db (migrating it if needed) and marks runs left over from a previous session
    /// as interrupted. Optional: every other method does it on first use.
    /// </summary>
    /// <exception cref="ClickyDatabaseException">The database can't be used.</exception>
    public Task InitializeAsync() => _store.Value;

    public async Task<IReadOnlyList<ClickyAgent>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await StoreAsync().ConfigureAwait(false)).ListClickysAsync(cancellationToken).ConfigureAwait(false);

    public async Task<ClickyAgent?> GetAsync(string clickyId, CancellationToken cancellationToken = default) =>
        await (await StoreAsync().ConfigureAwait(false)).GetClickyAsync(clickyId, cancellationToken).ConfigureAwait(false);

    /// <exception cref="ArgumentException">The draft has no name.</exception>
    public async Task<ClickyAgent> CreateAsync(ClickyDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft = draft.Normalize();
        var store = await StoreAsync().ConfigureAwait(false);
        var now = Now();
        var clicky = new ClickyAgent(NewId(), draft.Name, draft.Instructions, draft.Notes, draft.Color, draft.UsesComputer, draft.MaxSteps, 0, now, now, now);
        await store.InsertClickyAsync(clicky, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Clicky {Id} \"{Name}\" created", clicky.Id, clicky.Name);
        Raise(ClickyChange.ClickyAdded, clicky.Id);
        return clicky;
    }

    /// <summary>Changes take effect from the next run.</summary>
    /// <exception cref="ClickyNotFoundException">No such Clicky.</exception>
    public async Task<ClickyAgent> UpdateAsync(string clickyId, ClickyDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft = draft.Normalize();
        var store = await StoreAsync().ConfigureAwait(false);
        if (!await store.UpdateClickyAsync(clickyId, draft, Now(), cancellationToken).ConfigureAwait(false))
        {
            throw new ClickyNotFoundException($"There is no Clicky {clickyId}.");
        }

        Raise(ClickyChange.ClickyUpdated, clickyId);
        return await store.GetClickyAsync(clickyId, cancellationToken).ConfigureAwait(false) ?? throw new ClickyNotFoundException($"There is no Clicky {clickyId}.");
    }

    /// <summary>
    /// Stops its runs, then deletes the Clicky with its chat and history. Its files folder is left
    /// on disk for the user to keep or delete.
    /// </summary>
    public async Task DeleteAsync(string clickyId, CancellationToken cancellationToken = default)
    {
        var store = await StoreAsync().ConfigureAwait(false);
        var runs = _active.Values.Where(a => a.Run.ClickyId == clickyId).ToList();
        foreach (var run in runs)
        {
            run.Cancel();
        }

        await Task.WhenAll(runs.Select(r => r.Completion)).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (await store.DeleteClickyAsync(clickyId, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Clicky {Id} deleted", clickyId);
            Raise(ClickyChange.ClickyDeleted, clickyId);
        }
    }

    /// <summary>The newest <paramref name="limit"/> chat messages, oldest first.</summary>
    public async Task<IReadOnlyList<ClickyMessage>> GetMessagesAsync(string clickyId, int limit = 200, CancellationToken cancellationToken = default) =>
        await (await StoreAsync().ConfigureAwait(false)).GetMessagesAsync(clickyId, limit, cancellationToken).ConfigureAwait(false);

    /// <summary>Run history, newest first.</summary>
    public async Task<IReadOnlyList<ClickyRun>> GetRunsAsync(string clickyId, int limit = 50, CancellationToken cancellationToken = default) =>
        await (await StoreAsync().ConfigureAwait(false)).GetRunsAsync(clickyId, limit, cancellationToken).ConfigureAwait(false);

    public async Task<ClickyRun?> GetRunAsync(string runId, CancellationToken cancellationToken = default) =>
        _active.TryGetValue(runId, out var active)
            ? active.Run
            : await (await StoreAsync().ConfigureAwait(false)).GetRunAsync(runId, cancellationToken).ConfigureAwait(false);

    /// <summary>A run's activity log.</summary>
    public async Task<IReadOnlyList<ClickyRunStep>> GetStepsAsync(string runId, CancellationToken cancellationToken = default) =>
        await (await StoreAsync().ConfigureAwait(false)).GetStepsAsync(runId, cancellationToken).ConfigureAwait(false);

    /// <summary>The user has seen this Clicky's replies.</summary>
    public async Task MarkReadAsync(string clickyId, CancellationToken cancellationToken = default)
    {
        await (await StoreAsync().ConfigureAwait(false)).MarkReadAsync(clickyId, cancellationToken).ConfigureAwait(false);
        Raise(ClickyChange.ClickyUpdated, clickyId);
    }

    /// <summary>The user wrote in the Clicky's chat: posts the message and starts (or queues) a run for it.</summary>
    /// <returns>The run, already queued; follow it through <see cref="Changed"/>.</returns>
    /// <exception cref="ClickyNotFoundException">No such Clicky.</exception>
    public async Task<ClickyRun> SendAsync(string clickyId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var active = await StartAsync(clickyId, new ClickyRunRequest(text.Trim(), ClickyRunTrigger.Chat), cancellationToken).ConfigureAwait(false);
        return active.Run;
    }

    /// <summary>
    /// Runs a task and waits for it: posts the prompt in the Clicky's chat (as a routine turn for
    /// <see cref="ClickyRunTrigger.Routine"/>), waits for any run already going, runs it, and lands
    /// the reply in the chat as unread. Never speaks. This is what routines (M15) call.
    /// </summary>
    /// <param name="cancellationToken">Stops the run (recorded as cancelled).</param>
    /// <exception cref="ClickyNotFoundException">No such Clicky.</exception>
    /// <exception cref="ClaudeBackendException">Claude failed; the run is recorded as failed first.</exception>
    public async Task<ClickyRunOutcome> RunAsync(string clickyId, ClickyRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var active = await StartAsync(clickyId, request with { Prompt = request.Prompt.Trim() }, cancellationToken).ConfigureAwait(false);
        using (cancellationToken.Register(active.Cancel))
        {
            var result = await active.Completion.ConfigureAwait(false);
            if (result.BackendError is { } error)
            {
                ExceptionDispatchInfo.Throw(error);
            }

            return result.Outcome;
        }
    }

    /// <summary>Asks the same thing again as a new run.</summary>
    /// <exception cref="ClickyNotFoundException">No such run or Clicky.</exception>
    public async Task<ClickyRun> RetryAsync(string runId, CancellationToken cancellationToken = default)
    {
        var run = await GetRunAsync(runId, cancellationToken).ConfigureAwait(false) ?? throw new ClickyNotFoundException($"There is no run {runId}.");
        var trigger = run.Trigger == ClickyRunTrigger.Routine ? ClickyRunTrigger.Routine : ClickyRunTrigger.Chat;
        var active = await StartAsync(run.ClickyId, new ClickyRunRequest(run.Prompt, trigger, run.RoutineId, run.RoutineName), cancellationToken).ConfigureAwait(false);
        return active.Run;
    }

    /// <summary>Holds the run at its next step boundary (before its first step if it hasn't started).</summary>
    /// <returns>False if the run isn't active.</returns>
    public bool Pause(string runId)
    {
        if (!_active.TryGetValue(runId, out var active) || !active.RequestPause())
        {
            return false;
        }

        _logger.LogInformation("Run {Run} pausing", runId);
        Raise(ClickyChange.RunChanged, active.Run.ClickyId, runId);
        return true;
    }

    /// <returns>False if the run isn't active or wasn't paused.</returns>
    public bool Resume(string runId)
    {
        if (!_active.TryGetValue(runId, out var active) || !active.Resume())
        {
            return false;
        }

        _logger.LogInformation("Run {Run} resuming", runId);
        Raise(ClickyChange.RunChanged, active.Run.ClickyId, runId);
        return true;
    }

    /// <summary>Stops the run; what it said so far is kept in the chat.</summary>
    /// <returns>False if the run isn't active.</returns>
    public bool Cancel(string runId)
    {
        if (!_active.TryGetValue(runId, out var active))
        {
            return false;
        }

        _logger.LogInformation("Run {Run} stopping", runId);
        active.Cancel();
        return true;
    }

    /// <summary>Pause has been asked for and the run hasn't reached the boundary yet, or is held there.</summary>
    public bool IsPauseRequested(string runId) => _active.TryGetValue(runId, out var active) && active.PauseRequested;

    /// <summary>Stops every run (recorded as interrupted) and waits briefly for them to write their end state.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        var running = _active.Values.Select(a => (Task)a.Completion).ToArray();
        try
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("{Count} Clicky runs did not stop in time; they'll show as interrupted next start", running.Count(t => !t.IsCompleted));
        }

        _shutdown.Dispose();
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>What was said before a stop or failure, the step in progress included.</summary>
    private static string Partial(StringBuilder reply, RunProgress progress)
    {
        var done = reply.ToString().Trim();
        var current = progress.StepText.ToString().Trim();
        return done.Length == 0 ? current : current.Length == 0 ? done : done + "\n\n" + current;
    }

    // clickys.db keeps milliseconds; rounding here keeps returned records equal to what is read back.
    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(_time.GetUtcNow().ToUnixTimeMilliseconds());

    private static string Clip(string text, int max) => text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");

    /// <summary>One line for history and notifications.</summary>
    internal static string SummaryOf(string reply)
    {
        foreach (var raw in reply.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '>', '-', '*', ' ').Trim();
            if (line.Length > 0 && !line.StartsWith("```", StringComparison.Ordinal))
            {
                return Clip(line.Replace("**", string.Empty, StringComparison.Ordinal), 160);
            }
        }

        return "Done.";
    }

    internal static string Describe(ClickyAction action) => action switch
    {
        PointAction p => Format($"point at ({p.X:0}, {p.Y:0}){Label(p.Label)}"),
        HighlightAction h => Format($"highlight ({h.X:0}, {h.Y:0}, {h.Width:0}×{h.Height:0}){Label(h.Label)}"),
        DrawAction d => Format($"draw {d.Shape.ToString().ToLowerInvariant()}{Label(d.Label)}"),
        StepAction s => Format($"step {s.Index}: {s.Instruction}"),
        ClickAction c => Format($"{(c.Count == 2 ? "double-click" : c.Button == MouseButton.Right ? "right-click" : "click")} ({c.X:0}, {c.Y:0}){Label(c.Target)}"),
        TypeAction t => "type \"" + Clip(t.Text, 80) + "\"",
        KeyAction k => "press " + k.Keys,
        ScrollAction s => Format($"scroll {s.Direction.ToString().ToLowerInvariant()} {s.Amount}"),
        WaitAction w => Format($"wait {w.Ms} ms"),
        AskPermissionAction a => "ask permission: " + a.Action,
        DoneAction d => d.Success ? "done" + (d.Summary is { Length: > 0 } s ? ": " + s : string.Empty) : "couldn't finish" + (d.Summary is { Length: > 0 } f ? ": " + f : string.Empty),
        _ => action.Name,
    };

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Label(string? label) => string.IsNullOrWhiteSpace(label) ? string.Empty : " " + label.Trim();

    private async Task<ClickyStore> OpenAsync()
    {
        var database = await ClickyDatabase.OpenAsync(DatabasePath, _logger).ConfigureAwait(false);
        var store = new ClickyStore(database);
        var interrupted = await store.InterruptActiveRunsAsync(Now(), InterruptedError).ConfigureAwait(false);
        if (interrupted.Count > 0)
        {
            _logger.LogInformation("{Count} Clicky runs were cut off when Clicky last closed; marked interrupted", interrupted.Count);
        }

        return store;
    }

    private Task<ClickyStore> StoreAsync() => _store.Value;

    private async Task<ActiveRun> StartAsync(string clickyId, ClickyRunRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);
        var store = await StoreAsync().ConfigureAwait(false);
        _ = await store.GetClickyAsync(clickyId, cancellationToken).ConfigureAwait(false) ?? throw new ClickyNotFoundException($"There is no Clicky {clickyId}.");

        var now = Now();
        var run = new ClickyRun(
            NewId(),
            clickyId,
            request.Trigger,
            request.Prompt,
            ClickyRunStatus.Queued,
            now,
            RoutineId: request.RoutineId,
            RoutineName: request.RoutineName,
            ScheduledFor: request.ScheduledFor);
        await store.InsertRunAsync(run, cancellationToken).ConfigureAwait(false);
        var role = request.Trigger == ClickyRunTrigger.Routine ? ClickyMessageRole.Routine : ClickyMessageRole.User;
        await store.AddMessageAsync(clickyId, run.Id, role, request.Prompt, now, cancellationToken).ConfigureAwait(false);

        var active = new ActiveRun(run, _shutdown.Token);
        _active[run.Id] = active;
        _logger.LogInformation("Run {Run} queued for Clicky {Clicky} ({Trigger})", run.Id, clickyId, request.Trigger);
        Raise(ClickyChange.MessageAdded, clickyId, run.Id);
        Raise(ClickyChange.RunChanged, clickyId, run.Id);

        active.Completion = Task.Run(() => ExecuteAsync(store, active, request), CancellationToken.None);
        return active;
    }

    private async Task<RunResult> ExecuteAsync(ClickyStore store, ActiveRun active, ClickyRunRequest request)
    {
        var token = active.Token;
        var clickyId = active.Run.ClickyId;
        var turn = _clickyTurns.GetOrAdd(clickyId, _ => new SemaphoreSlim(1, 1));
        var holdsTurn = false;
        var holdsComputer = false;
        var reply = new StringBuilder();
        var progress = new RunProgress();
        ClickyAgent? clicky = null;
        RunResult result;

        try
        {
            await turn.WaitAsync(token).ConfigureAwait(false);
            holdsTurn = true;

            clicky = await store.GetClickyAsync(clickyId, token).ConfigureAwait(false) ?? throw new OperationCanceledException("The Clicky was deleted.");
            var acts = clicky.UsesComputer && _actor is not ChatOnlyClickyActor;
            if (acts)
            {
                await _computer.WaitAsync(token).ConfigureAwait(false);
                holdsComputer = true;
            }

            await WaitIfPausedAsync(store, active, token).ConfigureAwait(false);
            await UpdateAsync(store, active, r => r with { Status = ClickyRunStatus.Running, StartedAt = Now() }).ConfigureAwait(false);

            var conversation = await ConversationAsync(store, clicky, acts, active.Run.Id, token).ConfigureAwait(false);
            var screenshot = acts ? await ScreenshotAsync(store, active, token).ConfigureAwait(false) : null;
            var extraTools = _tools?.ToolsFor(clicky, request.Trigger);
            var claudeRequest = new ClaudeRequest(ClickyPrompt.Request(request), RequestSource.Agent, screenshot, Tools: extraTools);

            await StepAsync(store, active, ClickyStepKind.Request, screenshot is not null ? "Asked Claude, with a screenshot" : "Asked Claude").ConfigureAwait(false);
            await foreach (var replyEvent in _model.ReplyAsync(conversation, claudeRequest, token).ConfigureAwait(false))
            {
                await HandleAsync(store, active, replyEvent, reply, progress).ConfigureAwait(false);
            }

            // The Clicky's own tools (routines) are answered here, between replies, until Claude stops
            // calling them or asks for something only the actor can do.
            while (_tools is not null && progress.Last is { } latest && progress.Steps < clicky.MaxSteps && ToolCalls(latest) is { Count: > 0 } calls)
            {
                var results = new List<ToolOutcome>(calls.Count);
                foreach (var (action, toolCallId) in calls)
                {
                    var outcome = await _tools.CallAsync(clicky, request.Trigger, action, toolCallId, token).ConfigureAwait(false);
                    results.Add(outcome);
                    await StepAsync(store, active, outcome.IsError ? ClickyStepKind.Rejected : ClickyStepKind.Action, Format($"{outcome.Name}: {outcome.Text}")).ConfigureAwait(false);
                }

                await WaitIfPausedAsync(store, active, token).ConfigureAwait(false);
                var next = new ClaudeRequest(ToolsAnsweredText, RequestSource.Agent, Step: new AgentStep(progress.Steps, clicky.MaxSteps, results), Tools: extraTools);
                await StepAsync(store, active, ClickyStepKind.Request, "Asked Claude, with the results").ConfigureAwait(false);
                await foreach (var replyEvent in _model.ReplyAsync(conversation, next, token).ConfigureAwait(false))
                {
                    await HandleAsync(store, active, replyEvent, reply, progress).ConfigureAwait(false);
                }
            }

            if (acts && progress.Last is { } first && _actor.WantsToAct(first) && progress.Steps < clicky.MaxSteps)
            {
                // The actor stops at the cap itself; the checks here are the backstop.
                var options = new ClickyActorOptions(clicky.MaxSteps - progress.Steps);
                await using var steps = _actor.ContinueAsync(conversation, first, screenshot, options, token).GetAsyncEnumerator(token);
                while (true)
                {
                    // Steps end with ReplyCompleted; holding here keeps the actor from starting the next one.
                    // At the cap, don't let the actor start (or act on) another step.
                    if (progress.AtBoundary && progress.Steps >= clicky.MaxSteps)
                    {
                        break;
                    }

                    if (progress.AtBoundary)
                    {
                        await WaitIfPausedAsync(store, active, token).ConfigureAwait(false);
                    }

                    if (!await steps.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    await HandleAsync(store, active, steps.Current, reply, progress).ConfigureAwait(false);
                }
            }

            // Out of steps while the last reply still wanted to act.
            if (acts && progress.Steps >= clicky.MaxSteps && progress.Last is { } last && _actor.WantsToAct(last) && progress.Done is null)
            {
                progress.Capped = true;
                await StepAsync(store, active, ClickyStepKind.Control, Format($"Stopped after {clicky.MaxSteps} steps (this Clicky's limit)")).ConfigureAwait(false);
            }

            var text = reply.ToString().Trim();
            if (text.Length == 0)
            {
                text = progress.Done?.Summary is { Length: > 0 } summary ? summary : "(No reply.)";
            }

            await store.AddMessageAsync(clickyId, active.Run.Id, ClickyMessageRole.Assistant, text, Now(), CancellationToken.None).ConfigureAwait(false);
            Raise(ClickyChange.MessageAdded, clickyId, active.Run.Id);

            var succeeded = progress.Last?.Stop is not ReplyStop.Refused and not ReplyStop.MaxTokens && progress.Done?.Success != false && !progress.Capped;
            var outcomeSummary = progress.Done?.Summary is { Length: > 0 } done ? Clip(done, 160) : SummaryOf(text);
            await FinishAsync(store, active, ClickyRunStatus.Completed, outcomeSummary, null).ConfigureAwait(false);
            result = new(new(active.Run.Id, ClickyRunStatus.Completed, succeeded, outcomeSummary, text), null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || clicky is null)
        {
            var status = _shutdown.IsCancellationRequested ? ClickyRunStatus.Interrupted : ClickyRunStatus.Cancelled;
            var partial = Partial(reply, progress);
            var note = status == ClickyRunStatus.Interrupted ? InterruptedError : "Stopped.";
            await SafeAsync(async () =>
            {
                if (partial.Length > 0)
                {
                    await store.AddMessageAsync(clickyId, active.Run.Id, ClickyMessageRole.Assistant, partial, Now(), CancellationToken.None).ConfigureAwait(false);
                }

                await StepAsync(store, active, ClickyStepKind.Control, note).ConfigureAwait(false);
                await FinishAsync(store, active, status, note, status == ClickyRunStatus.Interrupted ? InterruptedError : null).ConfigureAwait(false);
            }).ConfigureAwait(false);
            Raise(ClickyChange.MessageAdded, clickyId, active.Run.Id);
            result = new(new(active.Run.Id, status, false, note, partial.Length > 0 ? partial : null), null);
        }
        catch (ClaudeBackendException ex)
        {
            _logger.LogWarning("Run {Run} failed: {Failure}: {Message}", active.Run.Id, ex.Failure, ex.Message);
            await FailAsync(store, active, ex.Message, Partial(reply, progress)).ConfigureAwait(false);
            result = new(new(active.Run.Id, ClickyRunStatus.Failed, false, ex.Message, null), ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Run {Run} failed", active.Run.Id);
            await FailAsync(store, active, "Something went wrong: " + ex.Message, Partial(reply, progress)).ConfigureAwait(false);
            result = new(new(active.Run.Id, ClickyRunStatus.Failed, false, ex.Message, null), null);
        }
        finally
        {
            if (holdsComputer)
            {
                _computer.Release();
            }

            if (holdsTurn)
            {
                turn.Release();
            }

            _active.TryRemove(active.Run.Id, out _);
            active.Dispose();
        }

        Raise(ClickyChange.RunChanged, clickyId, active.Run.Id);
        if (clicky is not null)
        {
            try
            {
                RunFinished?.Invoke(this, new ClickyRunFinishedEventArgs(clicky, active.Run, result.Outcome));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "RunFinished handler failed");
            }
        }

        return result;
    }

    /// <summary>
    /// The calls in <paramref name="completed"/> that the Clicky's own tools answer, with their tool
    /// call ids; none when the reply also wants something only the actor can do (it runs those next).
    /// </summary>
    private List<(ClickyAction Action, string? ToolCallId)> ToolCalls(ReplyCompleted completed)
    {
        if (_tools is null || completed.Actions.Any(a => a.NeedsResult && !_tools.Answers(a)))
        {
            return [];
        }

        return [.. completed.Actions
            .Where(_tools.Answers)
            .Select(a => (a, (completed.Calls.FirstOrDefault(c => ReferenceEquals(c.Action, a)) ?? completed.Calls.FirstOrDefault(c => Equals(c.Action, a)))?.Call.Id))];
    }

    private async Task<Conversation> ConversationAsync(ClickyStore store, ClickyAgent clicky, bool acts, string runId, CancellationToken token)
    {
        var messages = await store.GetMessagesAsync(clicky.Id, (ClickyPrompt.HistoryTurns * 3) + 1, token).ConfigureAwait(false);
        var history = ClickyPrompt.History([.. messages.Where(m => m.RunId != runId)]);

        var userMemory = string.Empty;
        if (_memory is not null)
        {
            try
            {
                userMemory = await _memory.GetPromptAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Could not read memory for a Clicky run; going on without it");
            }
        }

        var context = ClickyPrompt.Context(clicky, Workspace.DescribeForPrompt(clicky.Id), userMemory, Now(), acts);
        var conversation = new Conversation(_time, context);
        var limits = new HistoryLimits();
        foreach (var turn in history)
        {
            conversation.Append(turn, limits);
        }

        return conversation;
    }

    private async Task<RequestScreenshot?> ScreenshotAsync(ClickyStore store, ActiveRun active, CancellationToken token)
    {
        if (_capture is null)
        {
            return null;
        }

        try
        {
            var shot = await _capture.CaptureAsync(new CaptureTarget(CaptureMode.MonitorUnderCursor), token).ConfigureAwait(false);
            await StepAsync(store, active, ClickyStepKind.Screenshot, Format($"Screenshot {shot.Geometry.ImageWidth}×{shot.Geometry.ImageHeight}")).ConfigureAwait(false);
            return new RequestScreenshot(shot.Png, shot.Geometry);
        }
        catch (CaptureException ex)
        {
            _logger.LogWarning(ex, "Run {Run}: no screenshot", active.Run.Id);
            await StepAsync(store, active, ClickyStepKind.Error, "Couldn't take a screenshot: " + ex.Message).ConfigureAwait(false);
            return null;
        }
    }

    private async Task HandleAsync(ClickyStore store, ActiveRun active, ReplyEvent replyEvent, StringBuilder reply, RunProgress progress)
    {
        progress.AtBoundary = false;
        switch (replyEvent)
        {
            case ReplyStarted started:
                active.Update(r => r with { Model = started.Model });
                break;
            case ReplyText text:
                progress.StepText.Append(text.Text);
                break;
            case ReplyAction { Action: var action }:
                if (action is DoneAction done)
                {
                    progress.Done = done;
                }

                await StepAsync(store, active, action is AskPermissionAction ? ClickyStepKind.Rejected : ClickyStepKind.Action, Describe(action)).ConfigureAwait(false);
                break;
            case ReplyActionRejected rejected:
                await StepAsync(store, active, ClickyStepKind.Rejected, $"{rejected.Name}: {rejected.Reason}").ConfigureAwait(false);
                break;
            case ReplyCompleted completed:
                progress.Steps++;
                progress.Last = completed;
                progress.AtBoundary = true;
                progress.Done = completed.Actions.OfType<DoneAction>().LastOrDefault() ?? progress.Done;

                var stepText = progress.StepText.ToString().Trim();
                progress.StepText.Clear();
                if (stepText.Length > 0)
                {
                    if (reply.Length > 0)
                    {
                        reply.Append("\n\n");
                    }

                    reply.Append(stepText);
                    await StepAsync(store, active, ClickyStepKind.Text, Clip(stepText, MaxStepChars)).ConfigureAwait(false);
                }

                if (completed.Stop is ReplyStop.Refused or ReplyStop.MaxTokens)
                {
                    await StepAsync(store, active, ClickyStepKind.Error, completed.Stop == ReplyStop.Refused ? "Claude declined this request" : "The reply hit the length limit").ConfigureAwait(false);
                }

                active.Update(r => r with
                {
                    Steps = progress.Steps,
                    Model = completed.Model,
                    InputTokens = r.InputTokens + completed.Usage.InputTokens + completed.Usage.CacheReadTokens + completed.Usage.CacheWriteTokens,
                    OutputTokens = r.OutputTokens + completed.Usage.OutputTokens,
                    CostUsd = completed.Usage.CostUsd is { } cost ? (r.CostUsd ?? 0) + cost : r.CostUsd,
                });
                await store.UpdateRunAsync(active.Run, CancellationToken.None).ConfigureAwait(false);
                Raise(ClickyChange.RunChanged, active.Run.ClickyId, active.Run.Id);
                break;
        }
    }

    private async Task WaitIfPausedAsync(ClickyStore store, ActiveRun active, CancellationToken token)
    {
        if (active.PausedTask is not { } paused)
        {
            return;
        }

        var before = active.Run.Status;
        await UpdateAsync(store, active, r => r with { Status = ClickyRunStatus.Paused }).ConfigureAwait(false);
        await StepAsync(store, active, ClickyStepKind.Control, "Paused").ConfigureAwait(false);
        await paused.WaitAsync(token).ConfigureAwait(false);
        await UpdateAsync(store, active, r => r with { Status = before == ClickyRunStatus.Queued ? ClickyRunStatus.Queued : ClickyRunStatus.Running }).ConfigureAwait(false);
        await StepAsync(store, active, ClickyStepKind.Control, "Resumed").ConfigureAwait(false);
    }

    private async Task FailAsync(ClickyStore store, ActiveRun active, string message, string partial)
    {
        await SafeAsync(async () =>
        {
            if (partial.Length > 0)
            {
                await store.AddMessageAsync(active.Run.ClickyId, active.Run.Id, ClickyMessageRole.Assistant, partial, Now(), CancellationToken.None).ConfigureAwait(false);
            }

            await store.AddMessageAsync(active.Run.ClickyId, active.Run.Id, ClickyMessageRole.System, "Couldn't finish: " + message, Now(), CancellationToken.None).ConfigureAwait(false);
            await StepAsync(store, active, ClickyStepKind.Error, message).ConfigureAwait(false);
            await FinishAsync(store, active, ClickyRunStatus.Failed, Clip(message, 160), message).ConfigureAwait(false);
        }).ConfigureAwait(false);
        Raise(ClickyChange.MessageAdded, active.Run.ClickyId, active.Run.Id);
    }

    private Task FinishAsync(ClickyStore store, ActiveRun active, ClickyRunStatus status, string summary, string? error) =>
        UpdateAsync(store, active, r => r with { Status = status, EndedAt = Now(), Summary = summary, Error = error ?? r.Error });

    private async Task UpdateAsync(ClickyStore store, ActiveRun active, Func<ClickyRun, ClickyRun> change)
    {
        active.Update(change);

        // End states must be written even when the run was cancelled.
        await store.UpdateRunAsync(active.Run, CancellationToken.None).ConfigureAwait(false);
        Raise(ClickyChange.RunChanged, active.Run.ClickyId, active.Run.Id);
    }

    private async Task StepAsync(ClickyStore store, ActiveRun active, ClickyStepKind kind, string text)
    {
        await store.AddStepAsync(active.Run.Id, Now(), kind, Clip(text, MaxStepChars), CancellationToken.None).ConfigureAwait(false);
        Raise(ClickyChange.StepAdded, active.Run.ClickyId, active.Run.Id);
    }

    // Writing a run's end state must not hide why it ended.
    private async Task SafeAsync(Func<Task> write)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Could not record the end of a Clicky run");
        }
    }

    private void Raise(ClickyChange change, string clickyId, string? runId = null)
    {
        try
        {
            Changed?.Invoke(this, new ClickyChangedEventArgs(change, clickyId, runId));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Clicky change handler failed");
        }
    }

    private sealed record RunResult(ClickyRunOutcome Outcome, ClaudeBackendException? BackendError);

    private sealed class RunProgress
    {
        public StringBuilder StepText { get; } = new();

        public int Steps { get; set; }

        public ReplyCompleted? Last { get; set; }

        public DoneAction? Done { get; set; }

        public bool AtBoundary { get; set; }

        public bool Capped { get; set; }
    }

    /// <summary>A run in flight: its latest state, its stop switch and its pause gate.</summary>
    private sealed class ActiveRun : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _cancel;
        private TaskCompletionSource? _resume;

        public ActiveRun(ClickyRun run, CancellationToken shutdown)
        {
            Run = run;
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            Token = _cancel.Token;
        }

        public ClickyRun Run
        {
            get
            {
                lock (_gate)
                {
                    return field;
                }
            }

            private set;
        }

        public CancellationToken Token { get; }

        public Task<RunResult> Completion { get; set; } = null!;

        public bool PauseRequested
        {
            get
            {
                lock (_gate)
                {
                    return _resume is not null;
                }
            }
        }

        /// <summary>Completes on resume; null when not paused.</summary>
        public Task? PausedTask
        {
            get
            {
                lock (_gate)
                {
                    return _resume?.Task;
                }
            }
        }

        public void Update(Func<ClickyRun, ClickyRun> change)
        {
            lock (_gate)
            {
                Run = change(Run);
            }
        }

        public bool RequestPause()
        {
            lock (_gate)
            {
                if (_resume is not null || Token.IsCancellationRequested)
                {
                    return false;
                }

                _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return true;
            }
        }

        public bool Resume()
        {
            TaskCompletionSource? resume;
            lock (_gate)
            {
                resume = _resume;
                _resume = null;
            }

            resume?.TrySetResult();
            return resume is not null;
        }

        public void Cancel()
        {
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }

        public void Dispose() => _cancel.Dispose();
    }
}
