using System.Threading.Channels;
using Clicky.Core.Hotkeys;
using Clicky.Platform.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Hotkeys;

/// <summary>
/// <see cref="IHotkeyService"/> on a low-level keyboard hook. The <see cref="GestureEngine"/> lives on
/// the hook thread; events leave through a channel and are raised on a separate pump task, so a slow
/// subscriber can never stall typing.
/// </summary>
/// <remarks>
/// <para>Missed key-ups: when focus moves to an elevated window (UIPI) or the secure desktop, our hook
/// stops seeing keys, so a key-up can go missing and a hold would never end. While a hold is active
/// we compare against GetAsyncKeyState every 100 ms, and we reconcile on every foreground change.
/// Session lock/unlock resets everything.</para>
/// <para>Elevated windows: nothing reaches our hook while one has focus. That is a Windows rule we
/// cannot work around without running elevated, so we log it once per window to explain "dead"
/// hotkeys.</para>
/// </remarks>
public sealed class WindowsHotkeyService : IHotkeyService, IDisposable
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMilliseconds(100);

    private const int MaxRememberedWindows = 256;

    private readonly ILogger<WindowsHotkeyService> _logger;
    private readonly LowLevelKeyboardHook _hook;
    private readonly Channel<HotkeyEvent> _events = Channel.CreateUnbounded<HotkeyEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    // Hook-thread state.
    private readonly GestureEngine _engine;
    private readonly HashSet<nint> _elevatedWindowsLogged = [];
    private nuint _deadlineTimer;
    private nuint _reconcileTimer;
    private bool _engineSuspended;

    // Shared with other threads.
    private readonly Lock _gate = new();
    private int _suspendCount;
    private Action<KeyEvent>? _recorder;

    private Task? _pump;
    private bool _started;
    private bool _disposed;

    public WindowsHotkeyService(ILogger<WindowsHotkeyService> logger)
    {
        _logger = logger;
        _engine = new GestureEngine(e => _events.Writer.TryWrite(e));
        _hook = new LowLevelKeyboardHook(OnKey, OnForegroundChanged, logger);
    }

    public event EventHandler<HotkeyEvent>? Triggered;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _pump = Task.Run(PumpAsync);
        _hook.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _logger.LogInformation(
            "Keyboard hook installed{Elevated}",
            ProcessElevation.IsCurrentProcessElevated ? " (Clicky is elevated)" : string.Empty);
    }

    public void UpdateBindings(IReadOnlyList<HotkeyBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        HotkeyBinding[] copy = [.. bindings];
        _hook.Post(() =>
        {
            _engine.UpdateBindings(copy, Environment.TickCount64);
            ScheduleTimers();
            _logger.LogInformation("Hotkeys: {Bindings}", copy.Length == 0 ? "none" : string.Join(", ", copy.Select(b => $"{b.Action} = {b.Gesture.ToDisplayString()}")));
        });
    }

    public IDisposable Suspend()
    {
        lock (_gate)
        {
            _suspendCount++;
        }

        _hook.Post(ApplySuspension);
        return new Releaser(() =>
        {
            lock (_gate)
            {
                _suspendCount--;
            }

            _hook.Post(ApplySuspension);
        });
    }

    public IDisposable BeginRecording(Action<KeyEvent> onKey)
    {
        ArgumentNullException.ThrowIfNull(onKey);
        lock (_gate)
        {
            _recorder = onKey;
        }

        _hook.Post(ApplySuspension);
        return new Releaser(() =>
        {
            lock (_gate)
            {
                if (_recorder == onKey)
                {
                    _recorder = null;
                }
            }

            _hook.Post(ApplySuspension);
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }

        _hook.Dispose();
        _events.Writer.TryComplete();
        _pump?.Wait(TimeSpan.FromSeconds(1));
    }

    // Hook thread. Runs for every key on the system: keep it allocation-light and never block.
    private KeyVerdict OnKey(KeyEvent key)
    {
        var recorder = Volatile.Read(ref _recorder);
        if (recorder is not null)
        {
            // Only while one of our windows has focus, so a recorder that lost focus without being
            // disposed can never eat the user's typing elsewhere.
            if (key.IsInjected || !IsOwnWindow(GetForegroundWindow()))
            {
                return KeyVerdict.Pass;
            }

            recorder(key);
            return KeyVerdict.Swallow;
        }

        if (_engineSuspended)
        {
            return KeyVerdict.Pass;
        }

        var swallow = _engine.HandleKey(key);
        var mask = _engine.MaskKeyRequested;
        ScheduleTimers();
        return mask ? KeyVerdict.SwallowAndReplayAfterMask : swallow ? KeyVerdict.Swallow : KeyVerdict.Pass;
    }

    // Hook thread.
    private void OnForegroundChanged(nint hwnd)
    {
        if (_engine.HasKeysDown)
        {
            Reconcile("focus changed");
        }

        if (ProcessElevation.IsCurrentProcessElevated || hwnd == 0 || _elevatedWindowsLogged.Contains(hwnd))
        {
            return;
        }

        if (ProcessElevation.IsWindowElevated(hwnd, out var process))
        {
            if (_elevatedWindowsLogged.Count >= MaxRememberedWindows)
            {
                _elevatedWindowsLogged.Clear();
            }

            _elevatedWindowsLogged.Add(hwnd);
            _logger.LogWarning(
                "\"{Title}\" ({Process}) is running as administrator. Windows does not pass keystrokes from it to Clicky, so hotkeys will not work while it has focus. Run Clicky as administrator if you need them there.",
                ProcessElevation.GetWindowTitle(hwnd),
                process);
        }
    }

    // Hook thread.
    private void ApplySuspension()
    {
        bool suspend;
        lock (_gate)
        {
            suspend = _suspendCount > 0 || _recorder is not null;
        }

        if (suspend == _engineSuspended)
        {
            return;
        }

        _engineSuspended = suspend;
        _engine.Reset(Environment.TickCount64);
        ScheduleTimers();
        _logger.LogDebug("Hotkeys {State}", suspend ? "suspended" : "resumed");
    }

    // Hook thread. One-shot deadline timer for the engine, plus the reconcile poll while a hold is active.
    private void ScheduleTimers()
    {
        var deadline = _engine.NextDeadline;
        if (deadline is { } due)
        {
            var delay = TimeSpan.FromMilliseconds(Math.Max(1, due - Environment.TickCount64));
            _deadlineTimer = _hook.SetTimer(_deadlineTimer, delay, OnDeadline);
        }
        else if (_deadlineTimer != 0)
        {
            _hook.KillTimer(_deadlineTimer);
            _deadlineTimer = 0;
        }

        if (_engine.IsHoldActive && _reconcileTimer == 0)
        {
            _reconcileTimer = _hook.SetTimer(0, ReconcileInterval, () => Reconcile("poll"));
        }
        else if (!_engine.IsHoldActive && _reconcileTimer != 0)
        {
            _hook.KillTimer(_reconcileTimer);
            _reconcileTimer = 0;
        }
    }

    // Hook thread.
    private void OnDeadline()
    {
        _hook.KillTimer(_deadlineTimer);
        _deadlineTimer = 0;
        _engine.Tick(Environment.TickCount64);
        ScheduleTimers();
    }

    // Hook thread.
    private void Reconcile(string reason)
    {
        if (_engineSuspended)
        {
            return;
        }

        if (_engine.ReconcileHeld(static vk => (GetAsyncKeyState(vk) & 0x8000) != 0, Environment.TickCount64))
        {
            var foreground = GetForegroundWindow();
            var elevated = ProcessElevation.IsWindowElevated(foreground, out var process);
            _logger.LogWarning(
                "Released keys whose key-up never reached the hook ({Reason}); foreground is {Process}{Elevated}",
                reason,
                process,
                elevated ? ", which is elevated (UIPI blocks its keystrokes)" : string.Empty);
        }

        ScheduleTimers();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        // Lock, unlock, fast user switch, RDP connect/disconnect: key-ups may have gone to another
        // desktop, so start from a clean slate.
        _logger.LogInformation("Session switch ({Reason}); resetting hotkey state", e.Reason);
        _hook.Post(() =>
        {
            _engine.Reset(Environment.TickCount64);
            ScheduleTimers();
        });
    }

    private async Task PumpAsync()
    {
        await foreach (var e in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            _logger.LogDebug("Hotkey {Action} {Phase}", e.Action, e.Phase);
            var handlers = Triggered;
            if (handlers is null)
            {
                continue;
            }

            foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<HotkeyEvent>>())
            {
                try
                {
                    handler(this, e);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Hotkey handler {Handler} threw", handler.Method.Name);
                }
            }
        }
    }

    private static bool IsOwnWindow(nint hwnd) =>
        hwnd != 0 && GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid == (uint)Environment.ProcessId;

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
