using System.Collections.Concurrent;
using Clicky.Core.Routines;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Clicky.Tests.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Routines;

internal sealed class FakeRunner : IClickyRunner
{
    public ConcurrentQueue<RoutineRunRequest> Calls { get; } = new();

    public Func<RoutineRunRequest, CancellationToken, Task<RoutineRunResult>> Handler { get; set; } =
        (_, _) => Task.FromResult(RoutineRunResult.Success("All done.", "All done.\nFull text."));

    public IReadOnlyList<ClickyRef> Clickys { get; set; } = [];

    public Task<IReadOnlyList<ClickyRef>> ListClickysAsync(CancellationToken cancellationToken = default) => Task.FromResult(Clickys);

    public Task<RoutineRunResult> RunRoutineAsync(RoutineRunRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(request);
        return Handler(request, cancellationToken);
    }
}

internal sealed class FakeSignals : ISystemSignals
{
    public bool IsOnline { get; set; } = true;

    public event EventHandler? NetworkChanged;

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public event EventHandler? TimeChanged;

    public void GoOnline(bool online)
    {
        IsOnline = online;
        NetworkChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Suspend() => Suspending?.Invoke(this, EventArgs.Empty);

    public void Resume() => Resumed?.Invoke(this, EventArgs.Empty);

    public void ChangeTime() => TimeChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeQuiet : IQuietDetector
{
    public QuietReasons Reasons { get; set; }

    public QuietCheck Check(SpeechSettings settings) => new(Reasons, Reasons == QuietReasons.None ? null : "Teams");
}

/// <summary>An engine over a real routines.db in a temp folder, with a manual clock in UTC.</summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "clicky-routines-" + Guid.NewGuid().ToString("N"));

    public EngineHarness(DateTimeOffset start, RoutineSettings? settings = null)
    {
        Time = new TestClock(start);
        Settings = new FakeSettingsStore(ClickySettings.Default with { Routines = settings ?? new RoutineSettings() });
        Store = NewStore();
        Engine = NewEngine();
    }

    public TestClock Time { get; }

    public FakeSettingsStore Settings { get; }

    public FakeRunner Runner { get; } = new();

    public FakeSignals Signals { get; } = new();

    public FakeQuiet Quiet { get; } = new();

    public RoutineStore Store { get; private set; }

    public RoutineEngine Engine { get; private set; }

    public string DatabasePath => Path.Combine(_directory, "routines.db");

    public DateTimeOffset Now => Time.GetUtcNow();

    /// <summary>The engine's local time zone; change it and raise <see cref="FakeSignals.ChangeTime"/>.</summary>
    public TimeZoneInfo Zone { get; set; } = TestZones.Utc;

    public static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail($"Timed out waiting for: {what}");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Lets queued scheduling passes finish (they run on the thread pool after a timer or signal).</summary>
    public static Task Settle() => Task.Delay(150);

    public async Task<RoutineEngine> StartAsync()
    {
        await Engine.StartAsync();
        return Engine;
    }

    /// <summary>
    /// Time passing with the PC awake: the clock stops at every timer on the way, as a real one
    /// would, so the engine never sees an overdue timer (which it takes as a sleep).
    /// </summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        var target = Now + by;
        for (var i = 0; i < 10_000; i++)
        {
            await Engine.WaitIdleAsync();
            if (Time.NextDue is not { } due || due > target)
            {
                break;
            }

            Time.Advance(due - Now);
        }

        Time.Advance(target - Now);
        await Engine.WaitIdleAsync();
        await Settle();
    }

    /// <summary>The PC asleep: the clock jumps and every timer fires late, at once.</summary>
    public async Task SleepAsync(TimeSpan by)
    {
        Time.Advance(by);
        await Settle();
    }

    /// <summary>Quits and starts again over the same database, like Clicky closing and reopening.</summary>
    public async Task RestartAsync(TimeSpan closedFor)
    {
        await Engine.DisposeAsync();
        await Store.DisposeAsync();
        Time.Advance(closedFor);
        Store = NewStore();
        Engine = NewEngine();
        await Engine.StartAsync();
        await Settle();
    }

    public Routine Get(string id) => Engine.Find(id) ?? throw new InvalidOperationException("No such routine");

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        await Store.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private RoutineStore NewStore() => new(DatabasePath, NullLogger<RoutineStore>.Instance);

    private RoutineEngine NewEngine() =>
        new(Store, Runner, Signals, Settings, NullLogger<RoutineEngine>.Instance, Time, () => Zone, Quiet);
}

/// <summary>A clock that only moves when told to. Timers fire synchronously inside <see cref="Advance"/>.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private readonly List<Timer> _timers = [];
    private readonly Lock _gate = new();
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    /// <summary>When the earliest armed timer fires.</summary>
    public DateTimeOffset? NextDue
    {
        get
        {
            lock (_gate)
            {
                return _timers.Where(t => t.Due is not null).Select(t => t.Due).Min();
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        Timer[] due;
        lock (_gate)
        {
            _now += by < TimeSpan.Zero ? TimeSpan.Zero : by;
            due = [.. _timers.Where(t => t.Due is { } d && d <= _now)];
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class Timer(TestClock owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            }

            return true;
        }

        public void Fire()
        {
            lock (owner._gate)
            {
                if (Due is null || Due > owner._now)
                {
                    return;
                }

                Due = null;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
