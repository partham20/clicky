using System.Collections.Immutable;
using System.Globalization;
using Clicky.Core.Assistant;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Routines;

/// <summary>
/// Runs routines on time while Clicky is open. One timer, armed for the next due run (capped at
/// <see cref="MaxTimerWait"/> so a clock change or a sleep the timer didn't count is noticed), plus
/// the network, sleep and clock signals; nothing polls.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Missed runs.</b> Runs missed while the PC was asleep or off, or Clicky was closed, are
/// caught up once (the latest one) if it was due within settings.routines.catchUpHours; otherwise
/// they are recorded as skipped.</item>
/// <item><b>Waiting.</b> A due run waits for the network, for the PC to settle after waking
/// (<see cref="ResumeSettle"/>), for a call, screen share or presentation to end
/// (settings.routines.holdDuringCalls), and for a free slot (settings.routines.maxConcurrentRuns).
/// Run now skips all but the slot.</item>
/// <item><b>Failures.</b> <see cref="MaxFailuresInARow"/> failed runs in a row pause the routine with
/// the reason shown. A run cut short by sleep, or by losing the network, is retried rather than
/// counted.</item>
/// </list>
/// All state changes are serialized by one async gate and saved to <see cref="RoutineStore"/>
/// before anything else sees them. Events are raised outside the gate, on whatever thread made the
/// change; handlers must not block.
/// </remarks>
public sealed class RoutineEngine : IAsyncDisposable
{
    public const int MaxFailuresInARow = 3;

    /// <summary>The timer never sleeps longer than this, so sleep and clock changes are noticed even without a signal.</summary>
    public static readonly TimeSpan MaxTimerWait = TimeSpan.FromMinutes(15);

    /// <summary>After waking, the network often reports "up" before DNS, Wi-Fi sign-in or the VPN are back.</summary>
    public static readonly TimeSpan ResumeSettle = TimeSpan.FromSeconds(30);

    /// <summary>"When Clicky starts" routines wait this long so they don't compete with startup.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    /// <summary>How often a run held for a call checks again (there is no event for a call ending).</summary>
    public static readonly TimeSpan HoldRecheck = TimeSpan.FromMinutes(1);

    /// <summary>Runs this late (or later) are catch-ups rather than on time; a timer this overdue means the PC was asleep.</summary>
    public static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// After a sleep notice, scheduled runs wait for the wake-up. If Clicky is still running this long
    /// after the notice, the sleep didn't happen (vetoed) or the wake-up notice got lost.
    /// </summary>
    public static readonly TimeSpan SuspendExpiry = TimeSpan.FromMinutes(5);

    private const QuietReasons HoldReasons = QuietReasons.InCall | QuietReasons.ScreenSharing | QuietReasons.FullScreen;

    private readonly RoutineStore _store;
    private readonly IClickyRunner _runner;
    private readonly ISystemSignals _signals;
    private readonly ISettingsStore _settings;
    private readonly ILogger<RoutineEngine> _logger;
    private readonly TimeProvider _time;
    private readonly Func<TimeZoneInfo> _zone;
    private readonly IQuietDetector? _quiet;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Routine> _routines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveRun> _running = new(StringComparer.Ordinal);
    private readonly HashSet<string> _manual = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _waitReasons = new(StringComparer.Ordinal);
    private readonly List<Action> _pendingEvents = [];

    private ITimer? _timer;
    private DateTimeOffset _settleUntil;
    private DateTimeOffset? _suspendedAt;
    private DateTimeOffset? _armedFor;
    private bool _started;
    private bool _stopping;
    private ImmutableArray<Routine> _snapshot = [];
    private ImmutableDictionary<string, RoutineStatus> _statuses = ImmutableDictionary<string, RoutineStatus>.Empty;

    /// <param name="zone">The local time zone; read on every calculation so a time-zone change applies at once.</param>
    /// <param name="quiet">Detects calls and presentations for settings.routines.holdDuringCalls; null never holds.</param>
    public RoutineEngine(
        RoutineStore store,
        IClickyRunner runner,
        ISystemSignals signals,
        ISettingsStore settings,
        ILogger<RoutineEngine> logger,
        TimeProvider? time = null,
        Func<TimeZoneInfo>? zone = null,
        IQuietDetector? quiet = null)
    {
        _store = store;
        _runner = runner;
        _signals = signals;
        _settings = settings;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _zone = zone ?? (() => TimeZoneInfo.Local);
        _quiet = quiet;
    }

    /// <summary>Something about the routines changed (added, edited, ran, waiting…). Re-read <see cref="Routines"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>Clicky paused a routine by itself after <see cref="MaxFailuresInARow"/> failures.</summary>
    public event EventHandler<RoutinePausedEventArgs>? Paused;

    /// <summary>A run finished, whatever the outcome.</summary>
    public event EventHandler<RoutineRunFinishedEventArgs>? RunFinished;

    /// <summary>All routines, oldest first. Safe from any thread.</summary>
    public IReadOnlyList<Routine> Routines => _snapshot;

    /// <summary>What a routine is doing now, for the list. Safe from any thread.</summary>
    public RoutineStatus GetStatus(string routineId) =>
        _statuses.TryGetValue(routineId, out var status) ? status : new RoutineStatus(RoutineState.Scheduled, string.Empty);

    public Routine? Find(string routineId) => _snapshot.FirstOrDefault(r => r.Id == routineId);

    /// <summary>Loads the routines and starts the schedule. Call once.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var now = Now;
        var abandoned = await _store.CloseAbandonedRunsAsync(now, cancellationToken).ConfigureAwait(false);
        var loaded = await _store.ListAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var stored in loaded)
            {
                var routine = stored;
                if (routine.LastStatus == RoutineRunStatus.Running)
                {
                    routine = routine with { LastStatus = RoutineRunStatus.Interrupted, LastMessage = "Clicky closed before it finished." };
                }

                if (routine.Schedule.Kind == RoutineScheduleKind.AtStartup)
                {
                    routine = routine with { NextDueAt = now + StartupDelay, WaitingSince = null };
                }
                else if (routine.NextDueAt is null && routine.WaitingSince is null && !routine.IsFinished)
                {
                    routine = routine with { NextDueAt = routine.Schedule.NextAfter(now, Zone, routine.AnchorAt) };
                }

                if (routine != stored)
                {
                    await _store.SaveAsync(routine, cancellationToken).ConfigureAwait(false);
                }

                _routines[routine.Id] = routine;
            }

            _timer = _time.CreateTimer(_ => Kick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _signals.NetworkChanged += OnNetworkChanged;
            _signals.Resumed += OnResumed;
            _signals.Suspending += OnSuspending;
            _signals.TimeChanged += OnTimeChanged;
            _settings.Changed += OnSettingsChanged;
            _started = true;
            _logger.LogInformation("Routines: {Count} loaded{Abandoned}", _routines.Count, abandoned > 0 ? $", {abandoned} unfinished run(s) from last time closed" : string.Empty);
            await PumpLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    /// <exception cref="ArgumentException">The draft isn't valid; the message is for the user.</exception>
    public async Task<Routine> CreateAsync(RoutineDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Validate() is { } error)
        {
            throw new ArgumentException(error);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now;
            var routine = new Routine
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = draft.Name.Trim(),
                Prompt = draft.Prompt.Trim(),
                ClickyId = NullIfBlank(draft.ClickyId),
                Schedule = draft.Schedule,
                CreatedAt = now,
                UpdatedAt = now,
                AnchorAt = now,
                NextDueAt = draft.Schedule.NextAfter(now, Zone, now),
            };
            await SaveLockedAsync(routine).ConfigureAwait(false);
            _logger.LogInformation("Routine {Id} \"{Name}\" created: {Schedule}", routine.Id, routine.Name, routine.Schedule.Describe(CultureInfo.InvariantCulture));
            await PumpLockedAsync().ConfigureAwait(false);
            return routine;
        }
        finally
        {
            Release();
        }
    }

    /// <summary>Changes a routine. A new schedule starts counting from now.</summary>
    /// <exception cref="ArgumentException">The draft isn't valid; the message is for the user.</exception>
    /// <exception cref="KeyNotFoundException">No such routine.</exception>
    public async Task<Routine> UpdateAsync(string routineId, RoutineDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Validate() is { } error)
        {
            throw new ArgumentException(error);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now;
            var routine = Get(routineId) with
            {
                Name = draft.Name.Trim(),
                Prompt = draft.Prompt.Trim(),
                ClickyId = NullIfBlank(draft.ClickyId),
                UpdatedAt = now,
            };

            if (routine.Schedule != draft.Schedule)
            {
                routine = routine with
                {
                    Schedule = draft.Schedule,
                    AnchorAt = now,
                    NextDueAt = draft.Schedule.NextAfter(now, Zone, now),
                    WaitingSince = null,
                };
            }

            await SaveLockedAsync(routine).ConfigureAwait(false);
            await PumpLockedAsync().ConfigureAwait(false);
            return routine;
        }
        finally
        {
            Release();
        }
    }

    /// <summary>Deletes a routine and its history, stopping it if it is running. False if it wasn't there.</summary>
    public async Task<bool> DeleteAsync(string routineId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_routines.Remove(routineId))
            {
                return false;
            }

            _manual.Remove(routineId);
            if (_running.TryGetValue(routineId, out var run))
            {
                run.Cancel(CancelReason.User);
            }

            await _store.DeleteAsync(routineId, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Routine {Id} deleted", routineId);
            await PumpLockedAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            Release();
        }
    }

    /// <summary>Pauses or resumes a routine. Resuming clears the failure count and schedules the next run from now.</summary>
    /// <exception cref="KeyNotFoundException">No such routine.</exception>
    public async Task<Routine> SetEnabledAsync(string routineId, bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var routine = Get(routineId);
            var now = Now;
            routine = enabled
                ? routine with
                {
                    Enabled = true,
                    PausedReason = null,
                    ConsecutiveFailures = 0,
                    WaitingSince = null,
                    NextDueAt = routine.Schedule.Kind == RoutineScheduleKind.AtStartup ? null : routine.Schedule.NextAfter(now, Zone, routine.AnchorAt),
                    UpdatedAt = now,
                }
                : routine with { Enabled = false, PausedReason = null, WaitingSince = null, UpdatedAt = now };

            if (!enabled)
            {
                _manual.Remove(routineId);
            }

            await SaveLockedAsync(routine).ConfigureAwait(false);
            await PumpLockedAsync().ConfigureAwait(false);
            return routine;
        }
        finally
        {
            Release();
        }
    }

    /// <summary>
    /// Runs a routine now, paused or not, without moving its schedule. Doesn't wait for the network or
    /// a call to end, only for a free slot. Returns at once; no-op if it is already running.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such routine.</exception>
    public async Task RunNowAsync(string routineId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = Get(routineId);
            if (!_running.ContainsKey(routineId))
            {
                _manual.Add(routineId);
            }

            await PumpLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    /// <summary>Stops a routine's current run (recorded as cancelled, not a failure). False if it wasn't running.</summary>
    public async Task<bool> StopRunAsync(string routineId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _manual.Remove(routineId);
            if (!_running.TryGetValue(routineId, out var run))
            {
                return false;
            }

            run.Cancel(CancelReason.User);
            return true;
        }
        finally
        {
            Release();
        }
    }

    /// <summary>For tests: completes once every scheduling pass queued so far has run.</summary>
    internal async Task WaitIdleAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        Release();
    }

    public Task<IReadOnlyList<RoutineRun>> ListRunsAsync(string routineId, int limit = RoutineStore.KeepRunsPerRoutine, CancellationToken cancellationToken = default) =>
        _store.ListRunsAsync(routineId, limit, cancellationToken);

    /// <summary>Cancels running routines (they run again next time Clicky starts) and stops the schedule.</summary>
    public async Task StopAsync()
    {
        Task[] runs;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            _timer?.Dispose();
            if (_started)
            {
                _signals.NetworkChanged -= OnNetworkChanged;
                _signals.Resumed -= OnResumed;
                _signals.Suspending -= OnSuspending;
                _signals.TimeChanged -= OnTimeChanged;
                _settings.Changed -= OnSettingsChanged;
            }

            runs = [.. _running.Values.Select(r => r.Task)];
            foreach (var run in _running.Values)
            {
                run.Cancel(CancelReason.Shutdown);
            }
        }
        finally
        {
            Release();
        }

        // Each run records itself as it ends; give them a moment to do so.
        await Task.WhenAny(Task.WhenAll(runs), Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private TimeZoneInfo Zone => _zone();

    /// <summary>Schedules a pass over the routines. Safe from any thread, including timer callbacks.</summary>
    private void Kick() => _ = PumpAsync();

    private async Task PumpAsync()
    {
        try
        {
            await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await PumpLockedAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Routines: scheduling pass failed");
            _timer?.Change(HoldRecheck, Timeout.InfiniteTimeSpan);
        }
        finally
        {
            Release();
        }
    }

    /// <summary>The one scheduling pass: accept or skip due runs, start what can start, re-arm the timer.</summary>
    private async Task PumpLockedAsync()
    {
        if (!_started || _stopping)
        {
            return;
        }

        var now = Now;
        var settings = _settings.Current.Routines;
        var zone = Zone;
        _waitReasons.Clear();
        NoticeWake(now);

        if (settings.Enabled)
        {
            await AcceptDueRunsAsync(now, settings, zone).ConfigureAwait(false);
        }

        // Manual runs first, then waiting runs by how long they've been due.
        var candidates = _manual.Where(id => !_running.ContainsKey(id) && _routines.ContainsKey(id)).Select(id => (Id: id, Manual: true)).ToList();
        if (settings.Enabled)
        {
            candidates.AddRange(_routines.Values
                .Where(r => r.Enabled && r.WaitingSince is not null && !_running.ContainsKey(r.Id) && !_manual.Contains(r.Id))
                .OrderBy(r => r.NextDueAt ?? r.WaitingSince)
                .Select(r => (r.Id, Manual: false)));
        }

        string? blocked = null;
        var blockedChecked = false;
        var held = false;
        var slots = settings.MaxConcurrentRuns - _running.Count;
        foreach (var (id, manual) in candidates)
        {
            if (slots <= 0)
            {
                _waitReasons[id] = "Waiting for another routine to finish";
                continue;
            }

            if (!manual)
            {
                if (!blockedChecked)
                {
                    (blocked, held) = WhyNotNow(now, settings);
                    blockedChecked = true;
                }

                if (blocked is not null)
                {
                    _waitReasons[id] = blocked;
                    continue;
                }
            }

            await StartLockedAsync(_routines[id], manual, now, settings).ConfigureAwait(false);
            slots--;
        }

        ArmTimer(now, settings, held);
        Publish(now);
    }

    /// <summary>
    /// The timer is overdue (it doesn't count time asleep, and Modern Standby PCs don't always send
    /// the wake-up notice) or a sleep notice has expired: treat it as a wake-up.
    /// </summary>
    private void NoticeWake(DateTimeOffset now)
    {
        var overdue = _armedFor is { } armed && now - armed > LateAfter;
        var expired = _suspendedAt is { } suspended && now - suspended >= SuspendExpiry;
        if (!overdue && !expired)
        {
            return;
        }

        _logger.LogInformation("Routines: the PC seems to have been asleep ({Why}); settling for {Seconds} s", overdue ? "timer overdue" : "no wake-up notice", ResumeSettle.TotalSeconds);
        _suspendedAt = null;
        _armedFor = null;
        var settle = now + ResumeSettle;
        _settleUntil = settle > _settleUntil ? settle : _settleUntil;
    }

    private async Task AcceptDueRunsAsync(DateTimeOffset now, RoutineSettings settings, TimeZoneInfo zone)
    {
        // A timer always fires a little late, so "no catch-up" still allows a couple of minutes.
        var window = settings.CatchUpWindow > LateAfter ? settings.CatchUpWindow : LateAfter;
        foreach (var routine in _routines.Values.ToList())
        {
            if (!routine.Enabled || routine.WaitingSince is not null || routine.NextDueAt is not { } due || due > now || _running.ContainsKey(routine.Id))
            {
                continue;
            }

            var latest = routine.Schedule.LatestDue(due, now, zone, routine.AnchorAt);
            if (now - latest <= window)
            {
                await SaveLockedAsync(routine with { WaitingSince = now }).ConfigureAwait(false);
                continue;
            }

            var next = routine.Schedule.Kind == RoutineScheduleKind.AtStartup ? null : routine.Schedule.NextAfter(now, zone, routine.AnchorAt);
            var message = $"Missed the {RoutineFormat.When(latest, now, zone)} run while the PC was asleep or Clicky was closed.";
            _logger.LogInformation("Routine {Id}: skipping the run due {Due:o}, {Hours:0.#} h ago (catch-up window {Window} h)", routine.Id, latest, (now - latest).TotalHours, settings.CatchUpHours);
            await _store.AddRunAsync(new RoutineRun(0, routine.Id, latest, now, now, RoutineRunStatus.Skipped, RoutineTrigger.CatchUp, message, null)).ConfigureAwait(false);
            await SaveLockedAsync(routine with { NextDueAt = next, LastStatus = RoutineRunStatus.Skipped, LastMessage = message }).ConfigureAwait(false);
        }
    }

    /// <summary>Why scheduled runs can't start right now, or null; <c>held</c> is true for a call or presentation (no event ends those).</summary>
    private (string? Reason, bool Held) WhyNotNow(DateTimeOffset now, RoutineSettings settings)
    {
        if (_suspendedAt is not null)
        {
            return ("Waiting for the PC to wake up", false);
        }

        if (!_signals.IsOnline)
        {
            return ("Waiting for the network", false);
        }

        if (now < _settleUntil)
        {
            return ("Waiting for the PC to finish waking up", false);
        }

        if (settings.HoldDuringCalls && _quiet is not null)
        {
            var check = _quiet.Check(_settings.Current.Speech);
            var reasons = check.Reasons & HoldReasons;
            if (reasons != QuietReasons.None)
            {
                return ($"Waiting: {QuietPolicy.Describe(reasons, check.Detail)}", true);
            }
        }

        return (null, false);
    }

    private async Task StartLockedAsync(Routine routine, bool manual, DateTimeOffset now, RoutineSettings settings)
    {
        var zone = Zone;
        DateTimeOffset scheduledFor;
        RoutineTrigger trigger;
        if (manual)
        {
            _manual.Remove(routine.Id);
            scheduledFor = now;
            trigger = RoutineTrigger.Manual;
        }
        else
        {
            var due = routine.NextDueAt ?? routine.WaitingSince ?? now;
            scheduledFor = routine.Schedule.LatestDue(due, now, zone, routine.AnchorAt);
            trigger = routine.Schedule.Kind == RoutineScheduleKind.AtStartup ? RoutineTrigger.Startup
                : now - scheduledFor >= LateAfter ? RoutineTrigger.CatchUp
                : RoutineTrigger.Schedule;
            routine = routine with
            {
                NextDueAt = routine.Schedule.Kind == RoutineScheduleKind.AtStartup ? null : routine.Schedule.NextAfter(now, zone, routine.AnchorAt),
                WaitingSince = null,
            };
        }

        routine = routine with { LastRunAt = now, LastStatus = RoutineRunStatus.Running, LastMessage = null };
        await SaveLockedAsync(routine).ConfigureAwait(false);
        var runId = await _store.AddRunAsync(new RoutineRun(0, routine.Id, scheduledFor, now, null, RoutineRunStatus.Running, trigger, null, null)).ConfigureAwait(false);

        var request = new RoutineRunRequest(routine.Id, routine.Name, routine.Prompt, routine.ClickyId, scheduledFor, trigger == RoutineTrigger.CatchUp, trigger);
        var run = new ActiveRun(runId, request, settings.RunTimeout, _time, _stop.Token);
        _running[routine.Id] = run;
        _logger.LogInformation("Routine {Id} \"{Name}\" starting ({Trigger}, scheduled for {Scheduled:o})", routine.Id, routine.Name, trigger, scheduledFor);
        run.Task = Task.Run(() => ExecuteAsync(run));
    }

    private async Task ExecuteAsync(ActiveRun run)
    {
        RoutineRunResult? result = null;
        Exception? error = null;
        try
        {
            result = await _runner.RunRoutineAsync(run.Request, run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex;
        }

        try
        {
            await FinishAsync(run, result, error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Routine {Id}: could not record the end of the run", run.Request.RoutineId);
        }
        finally
        {
            run.Dispose();
        }
    }

    private async Task FinishAsync(ActiveRun run, RoutineRunResult? result, Exception? error)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var id = run.Request.RoutineId;
            _running.Remove(id);
            var now = Now;
            var outcome = Classify(run, result, error);
            var output = result?.Output;

            if (!_routines.TryGetValue(id, out var routine))
            {
                // Deleted while running; the history went with it.
                return;
            }

            routine = routine with { LastStatus = outcome.Status, LastMessage = outcome.Message };
            if (outcome.Retry && run.Request.Trigger != RoutineTrigger.Manual && routine.Enabled)
            {
                // Due again straight away; it waits for wake/network like any due run.
                routine = routine with { NextDueAt = now, WaitingSince = now };
            }

            if (outcome.Status == RoutineRunStatus.Succeeded)
            {
                routine = routine with { ConsecutiveFailures = 0 };
            }
            else if (outcome.Status == RoutineRunStatus.Failed)
            {
                routine = routine with { ConsecutiveFailures = routine.ConsecutiveFailures + 1 };
                if (routine.ConsecutiveFailures >= MaxFailuresInARow && routine.Enabled)
                {
                    var reason = $"Paused after {MaxFailuresInARow} failed runs in a row. Last error: {outcome.Message}";
                    routine = routine with { Enabled = false, PausedReason = reason, WaitingSince = null };
                    var paused = routine;
                    _pendingEvents.Add(() => Paused?.Invoke(this, new RoutinePausedEventArgs(paused, reason)));
                    _logger.LogWarning("Routine {Id} \"{Name}\" paused: {Reason}", id, routine.Name, reason);
                }
            }

            _logger.Log(
                outcome.Status == RoutineRunStatus.Failed ? LogLevel.Warning : LogLevel.Information,
                "Routine {Id} run {RunId} {Status}: {Message}",
                id,
                run.RunId,
                outcome.Status,
                outcome.Message);

            await SaveLockedAsync(routine).ConfigureAwait(false);
            await _store.FinishRunAsync(run.RunId, id, outcome.Status, now, outcome.Message, output).ConfigureAwait(false);
            var finished = routine;
            _pendingEvents.Add(() => RunFinished?.Invoke(this, new RoutineRunFinishedEventArgs(finished, run.RunId, outcome.Status, outcome.Message, output)));
            await PumpLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    private (RoutineRunStatus Status, string Message, bool Retry) Classify(ActiveRun run, RoutineRunResult? result, Exception? error)
    {
        if (result is { Succeeded: true })
        {
            return (RoutineRunStatus.Succeeded, OneLine(result.Summary) ?? "Done.", false);
        }

        if (result is not null)
        {
            return (RoutineRunStatus.Failed, OneLine(result.Summary) ?? "The Clicky couldn't finish it.", false);
        }

        if (error is null)
        {
            return run.Reason switch
            {
                CancelReason.Suspend => (RoutineRunStatus.Interrupted, "The PC went to sleep; it runs again after waking.", true),
                CancelReason.Shutdown => (RoutineRunStatus.Interrupted, "Clicky closed before it finished; it runs again next time.", true),
                CancelReason.User => (RoutineRunStatus.Cancelled, "Stopped.", false),
                _ => (RoutineRunStatus.Failed, $"Took longer than {FormatMinutes(run.Timeout)}, so it was stopped.", false),
            };
        }

        if (error is ClaudeBackendException { Failure: ClaudeFailure.Network } && !_signals.IsOnline)
        {
            return (RoutineRunStatus.Interrupted, "Lost the network; it runs again when it's back.", true);
        }

        var message = error is ClaudeBackendException ? error.Message : $"Something went wrong: {error.Message}";
        if (error is not ClaudeBackendException)
        {
            _logger.LogError(error, "Routine {Id} failed", run.Request.RoutineId);
        }

        return (RoutineRunStatus.Failed, OneLine(message) ?? "Failed.", false);
    }

    private void ArmTimer(DateTimeOffset now, RoutineSettings settings, bool held)
    {
        DateTimeOffset? wake = null;
        void Consider(DateTimeOffset at) => wake = wake is { } w && w <= at ? w : at;

        if (settings.Enabled)
        {
            foreach (var routine in _routines.Values)
            {
                if (routine.Enabled && routine.WaitingSince is null && routine.NextDueAt is { } due && !_running.ContainsKey(routine.Id))
                {
                    Consider(due);
                }
            }

            var waiting = _routines.Values.Any(r => r.Enabled && r.WaitingSince is not null && !_running.ContainsKey(r.Id));
            if (waiting && now < _settleUntil)
            {
                Consider(_settleUntil);
            }

            if (waiting && _suspendedAt is { } suspended)
            {
                Consider(suspended + SuspendExpiry);
            }

            if (waiting && held)
            {
                Consider(now + HoldRecheck);
            }
        }

        if (wake is null)
        {
            // Nothing scheduled: signals and edits kick the engine.
            _armedFor = null;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var delay = wake.Value - now;
        delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaxTimerWait ? MaxTimerWait : delay;
        _armedFor = now + delay;
        _timer?.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void Publish(DateTimeOffset now)
    {
        var settings = _settings.Current.Routines;
        var zone = Zone;
        var statuses = ImmutableDictionary.CreateBuilder<string, RoutineStatus>(StringComparer.Ordinal);
        foreach (var routine in _routines.Values)
        {
            statuses[routine.Id] = StatusOf(routine, now, settings, zone);
        }

        _statuses = statuses.ToImmutable();
        _snapshot = [.. _routines.Values.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal)];
        _pendingEvents.Add(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    private RoutineStatus StatusOf(Routine routine, DateTimeOffset now, RoutineSettings settings, TimeZoneInfo zone)
    {
        if (_running.ContainsKey(routine.Id))
        {
            return new(RoutineState.Running, "Running now…");
        }

        if (_waitReasons.TryGetValue(routine.Id, out var reason))
        {
            return new(RoutineState.Waiting, reason);
        }

        if (!routine.Enabled)
        {
            return new(RoutineState.Paused, routine.PausedReason ?? "Paused");
        }

        if (routine.IsFinished)
        {
            return new(RoutineState.Finished, routine.LastStatus == RoutineRunStatus.Skipped || routine.LastRunAt is null
                ? "Missed: Clicky wasn't running at the time"
                : $"Ran {RoutineFormat.When(routine.LastRunAt.Value, now, zone)}");
        }

        if (!settings.Enabled)
        {
            return new(RoutineState.Off, "Routines are turned off");
        }

        if (routine.WaitingSince is not null)
        {
            return new(RoutineState.Waiting, "Due now");
        }

        return routine.NextDueAt is { } next
            ? new(RoutineState.Scheduled, $"Next: {RoutineFormat.When(next, now, zone)}")
            : new(RoutineState.Scheduled, routine.Schedule.Kind == RoutineScheduleKind.AtStartup ? "Next: when Clicky starts" : "Not scheduled");
    }

    private async Task SaveLockedAsync(Routine routine)
    {
        await _store.SaveAsync(routine with { UpdatedAt = routine.UpdatedAt == default ? Now : routine.UpdatedAt }).ConfigureAwait(false);
        _routines[routine.Id] = routine;
    }

    private Routine Get(string routineId) =>
        _routines.TryGetValue(routineId, out var routine) ? routine : throw new KeyNotFoundException($"No routine with id {routineId}.");

    /// <summary>Releases the gate, then raises the events the locked work queued.</summary>
    private void Release()
    {
        Action[] events = [.. _pendingEvents];
        _pendingEvents.Clear();
        _gate.Release();
        foreach (var raise in events)
        {
            try
            {
                raise();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Routines: an event handler failed");
            }
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Kick();

    private void OnResumed(object? sender, EventArgs e) => _ = WithGateAsync(() =>
    {
        _logger.LogInformation("Routines: PC woke up; waiting {Seconds} s before running anything due", ResumeSettle.TotalSeconds);
        _suspendedAt = null;
        _armedFor = null;
        _settleUntil = Now + ResumeSettle;
    });

    // Runs cut short by sleep are retried after the wake-up, not counted as failures.
    private void OnSuspending(object? sender, EventArgs e) => _ = WithGateAsync(() =>
    {
        _logger.LogInformation("Routines: PC going to sleep; stopping {Count} run(s)", _running.Count);
        _suspendedAt = Now;
        foreach (var run in _running.Values)
        {
            run.Cancel(CancelReason.Suspend);
        }
    });

    /// <summary>Applies a change from a signal under the gate, then runs a scheduling pass.</summary>
    private async Task WithGateAsync(Action change)
    {
        try
        {
            await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            change();
            await PumpLockedAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Routines: handling a system signal failed");
        }
        finally
        {
            Release();
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e) => _ = RescheduleAfterClockChangeAsync();

    /// <summary>Upcoming runs are recomputed in the new local time; runs already due stay due.</summary>
    private async Task RescheduleAfterClockChangeAsync()
    {
        TimeZoneInfo.ClearCachedData();
        try
        {
            await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            var now = Now;
            foreach (var routine in _routines.Values.ToList())
            {
                if (routine.WaitingSince is null && routine.NextDueAt is { } due && due > now && routine.Schedule.Kind != RoutineScheduleKind.AtStartup)
                {
                    var next = routine.Schedule.NextAfter(now, Zone, routine.AnchorAt);
                    if (next != due)
                    {
                        await SaveLockedAsync(routine with { NextDueAt = next }).ConfigureAwait(false);
                    }
                }
            }

            _logger.LogInformation("Routines: clock or time zone changed; schedule recomputed");
            await PumpLockedAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Routines: could not reschedule after a clock change");
        }
        finally
        {
            Release();
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Routines != e.Current.Routines)
        {
            Kick();
        }
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..199] + "…" : line;
    }

    private static string FormatMinutes(TimeSpan span) => span.TotalMinutes == 1 ? "1 minute" : $"{span.TotalMinutes:0} minutes";

    private enum CancelReason
    {
        None,
        User,
        Suspend,
        Shutdown,
    }

    /// <summary>A run in progress: its cancellation (user, sleep, shutdown, timeout) and the reason it was cancelled.</summary>
    private sealed class ActiveRun : IDisposable
    {
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationTokenSource _cancel = new();
        private readonly CancellationTokenSource _linked;

        public ActiveRun(long runId, RoutineRunRequest request, TimeSpan timeout, TimeProvider time, CancellationToken stopping)
        {
            RunId = runId;
            Request = request;
            Timeout = timeout;
            _timeout = new CancellationTokenSource(timeout, time);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token, _cancel.Token, stopping);
        }

        public long RunId { get; }

        public RoutineRunRequest Request { get; }

        public TimeSpan Timeout { get; }

        public CancelReason Reason { get; private set; }

        public CancellationToken Token => _linked.Token;

        public Task Task { get; set; } = Task.CompletedTask;

        public void Cancel(CancelReason reason)
        {
            if (Reason == CancelReason.None)
            {
                Reason = reason;
            }

            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _linked.Dispose();
            _timeout.Dispose();
            _cancel.Dispose();
        }
    }
}

public enum RoutineState
{
    Scheduled,
    Waiting,
    Running,
    Paused,
    Finished,

    /// <summary>settings.routines.enabled is off.</summary>
    Off,
}

/// <param name="Text">One line for the list: "Next: tomorrow 8:00 AM", "Waiting for the network", the pause reason…</param>
public sealed record RoutineStatus(RoutineState State, string Text);

public sealed class RoutinePausedEventArgs(Routine routine, string reason) : EventArgs
{
    public Routine Routine { get; } = routine;

    public string Reason { get; } = reason;
}

public sealed class RoutineRunFinishedEventArgs(Routine routine, long runId, RoutineRunStatus status, string message, string? output) : EventArgs
{
    public Routine Routine { get; } = routine;

    public long RunId { get; } = runId;

    public RoutineRunStatus Status { get; } = status;

    public string Message { get; } = message;

    public string? Output { get; } = output;
}

/// <summary>Times as people say them: "today 8:00 AM", "tomorrow 7:30 AM", "Fri 9:00 AM", "24 Oct 8:00 AM".</summary>
public static class RoutineFormat
{
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var time = local.ToString("t", culture);
        var days = (local.Date - today).Days;
        return days switch
        {
            0 => $"today {time}",
            1 => $"tomorrow {time}",
            -1 => $"yesterday {time}",
            > 1 and < 7 => $"{local.ToString("ddd", culture)} {time}",
            _ when local.Year == today.Year => $"{local.ToString("d MMM", culture)} {time}",
            _ => $"{local.ToString("d MMM yyyy", culture)} {time}",
        };
    }
}
