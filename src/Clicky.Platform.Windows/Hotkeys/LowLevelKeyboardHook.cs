using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Clicky.Core.Hotkeys;
using Microsoft.Extensions.Logging;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Hotkeys;

internal enum KeyVerdict
{
    Pass,
    Swallow,

    /// <summary>Swallow, then inject the menu-mask key down/up followed by a copy of this key-up.</summary>
    SwallowAndReplayAfterMask,
}

/// <summary>
/// A dedicated thread that owns a WH_KEYBOARD_LL hook, a foreground-change WinEvent hook, thread
/// timers and a work queue, all driven by its own GetMessage loop.
/// </summary>
/// <remarks>
/// <para>Windows calls a low-level hook on the thread that installed it, inside that thread's message
/// loop, and drops the hook if a call takes longer than LowLevelHooksTimeout. A thread of our own
/// keeps the UI thread's work (layout, GC pauses on it) from ever delaying a keystroke.</para>
/// <para>Every callback (<c>onKey</c>, <c>onForeground</c>, timers, posted work) runs on this thread,
/// so the code behind it needs no locks. They must be quick and must not throw.</para>
/// <para>One instance per process: the unmanaged callbacks find it through a static.</para>
/// </remarks>
internal sealed class LowLevelKeyboardHook : IDisposable
{
    private const uint WmAppWork = WmApp + 1;
    private const uint WmAppReplay = WmApp + 2;

    // Marks the keys we inject so they are recognisable in a debugger; the engine already ignores
    // anything injected.
    private const nuint InjectionSignature = 0x434C_4B59; // "CLKY"

    private static LowLevelKeyboardHook? s_instance;

    private readonly Func<KeyEvent, KeyVerdict> _onKey;
    private readonly Action<nint> _onForeground;
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Dictionary<nuint, Action> _timers = [];
    private readonly ManualResetEventSlim _ready = new();

    private Thread? _thread;
    private uint _threadId;
    private nint _keyboardHook;
    private nint _foregroundHook;
    private int _installError;
    private bool _disposed;

    public LowLevelKeyboardHook(Func<KeyEvent, KeyVerdict> onKey, Action<nint> onForeground, ILogger logger)
    {
        _onKey = onKey;
        _onForeground = onForeground;
        _logger = logger;
    }

    public bool IsRunning => _keyboardHook != 0;

    /// <summary>Starts the thread and installs the hooks. Throws if the keyboard hook cannot be installed.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref s_instance, this, null) is not null)
        {
            throw new InvalidOperationException("Only one low-level keyboard hook per process.");
        }

        _thread = new Thread(Run) { Name = "Clicky hotkeys", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();

        if (_keyboardHook == 0)
        {
            Interlocked.CompareExchange(ref s_instance, null, this);
            throw new InvalidOperationException($"SetWindowsHookEx(WH_KEYBOARD_LL) failed with error {_installError}.");
        }
    }

    /// <summary>Runs <paramref name="action"/> on the hook thread. Safe from any thread; dropped after Dispose.</summary>
    public void Post(Action action)
    {
        if (_disposed || _threadId == 0)
        {
            return;
        }

        _work.Enqueue(action);
        PostThreadMessage(_threadId, WmAppWork, 0, 0);
    }

    /// <summary>
    /// Hook thread only. Starts (or restarts) a repeating timer; pass the id it returned last time to
    /// replace that timer rather than add another. Returns the timer id.
    /// </summary>
    public nuint SetTimer(nuint existingId, TimeSpan interval, Action callback)
    {
        VerifyHookThread();
        var ms = (uint)Math.Clamp(interval.TotalMilliseconds, 1, int.MaxValue);
        var id = NativeMethods.SetTimer(0, existingId, ms, 0);
        if (id == 0)
        {
            _logger.LogWarning("SetTimer failed with error {Error}", Marshal.GetLastPInvokeError());
            return 0;
        }

        if (existingId != 0 && existingId != id)
        {
            _timers.Remove(existingId);
        }

        _timers[id] = callback;
        return id;
    }

    /// <summary>Hook thread only.</summary>
    public void KillTimer(nuint id)
    {
        VerifyHookThread();
        if (id != 0 && _timers.Remove(id))
        {
            NativeMethods.KillTimer(0, id);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_thread is { IsAlive: true } thread)
        {
            PostThreadMessage(_threadId, WmQuit, 0, 0);
            if (!thread.Join(TimeSpan.FromSeconds(2)))
            {
                _logger.LogWarning("Hotkey thread did not stop within 2 s");
            }
        }

        _ready.Dispose();
    }

    private void VerifyHookThread()
    {
        if (GetCurrentThreadId() != _threadId)
        {
            throw new InvalidOperationException("Must be called on the hotkey thread.");
        }
    }

    private unsafe void Run()
    {
        _threadId = GetCurrentThreadId();

        // Creates the thread's message queue so PostThreadMessage works from here on.
        PeekMessage(out _, 0, 0, 0, PmNoRemove);

        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, &KeyboardProc, GetModuleHandle(null), 0);
        _installError = _keyboardHook == 0 ? Marshal.GetLastPInvokeError() : 0;
        if (_keyboardHook != 0)
        {
            _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, &ForegroundProc, 0, 0, WineventOutOfContext);
        }

        _ready.Set();
        if (_keyboardHook == 0)
        {
            return;
        }

        try
        {
            while (GetMessage(out var msg, 0, 0, 0) > 0)
            {
                if (msg.Hwnd != 0)
                {
                    DispatchMessage(msg);
                    continue;
                }

                switch (msg.Message)
                {
                    case WmTimer when _timers.TryGetValue(msg.WParam, out var callback):
                        RunSafely(callback);
                        break;
                    case WmAppWork:
                        while (_work.TryDequeue(out var work))
                        {
                            RunSafely(work);
                        }

                        break;
                    case WmAppReplay:
                        ReplayAfterMask((int)(msg.WParam & 0xFFFF), (int)(msg.WParam >> 16), (uint)msg.LParam);
                        break;
                }
            }
        }
        finally
        {
            foreach (var id in _timers.Keys)
            {
                NativeMethods.KillTimer(0, id);
            }

            _timers.Clear();
            if (_foregroundHook != 0)
            {
                UnhookWinEvent(_foregroundHook);
            }

            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
            Interlocked.CompareExchange(ref s_instance, null, this);
        }
    }

    private void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // Keep the loop alive: losing it would silently kill every hotkey.
            _logger.LogError(ex, "Hotkey thread work item failed");
        }
    }

    /// <summary>
    /// Injects mask down, mask up, then the swallowed key-up, in that order, as one SendInput batch so
    /// nothing can interleave. The shell then sees another key between Win down and Win up.
    /// </summary>
    private unsafe void ReplayAfterMask(int vk, int scanCode, uint hookFlags)
    {
        var inputs = stackalloc NativeInput[3];
        inputs[0] = KeyInput(VirtualKeys.MaskKey, 0, 0);
        inputs[1] = KeyInput(VirtualKeys.MaskKey, 0, KeyeventfKeyUp);
        inputs[2] = KeyInput(vk, scanCode, KeyeventfKeyUp | ((hookFlags & LlkhfExtended) != 0 ? KeyeventfExtendedKey : 0));

        var sent = SendInput(3, inputs, sizeof(NativeInput));
        if (sent != 3)
        {
            _logger.LogWarning("Menu-mask injection sent {Sent} of 3 inputs (error {Error}); the Start menu may open", sent, Marshal.GetLastPInvokeError());
        }
    }

    private static NativeInput KeyInput(int vk, int scanCode, uint flags) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeybdInput { Vk = (ushort)vk, Scan = (ushort)scanCode, Flags = flags, ExtraInfo = InjectionSignature },
        },
    };

    private static long ToMonotonicMs(uint eventTime)
    {
        // The hook reports GetTickCount() time (32-bit, wraps every 49.7 days). Re-base it onto
        // TickCount64 so it shares a clock with the timers.
        var now = Environment.TickCount64;
        var age = unchecked((uint)now - eventTime);
        return age < 60_000 ? now - age : now;
    }

    [UnmanagedCallersOnly]
    private static unsafe nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        var self = s_instance;
        if (code >= 0 && self is not null)
        {
            try
            {
                var info = (KbdLlHookStruct*)lParam;
                var key = new KeyEvent(
                    (int)info->VkCode,
                    (int)info->ScanCode,
                    IsDown: (info->Flags & LlkhfUp) == 0,
                    IsExtended: (info->Flags & LlkhfExtended) != 0,
                    IsInjected: (info->Flags & LlkhfInjected) != 0,
                    ToMonotonicMs(info->Time));

                switch (self._onKey(key))
                {
                    case KeyVerdict.Swallow:
                        return 1;
                    case KeyVerdict.SwallowAndReplayAfterMask:
                        // Posted, not injected here: input injected from inside the hook would be
                        // processed after the key-up we are holding, too late to mask it.
                        PostThreadMessage(self._threadId, WmAppReplay, (nuint)(key.Vk | (key.ScanCode << 16)), (nint)info->Flags);
                        return 1;
                }
            }
            catch (Exception ex)
            {
                // An exception escaping an UnmanagedCallersOnly method kills the process.
                self._logger.LogError(ex, "Keyboard hook callback failed; key passed through");
            }
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static void ForegroundProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        var self = s_instance;
        if (self is null)
        {
            return;
        }

        try
        {
            self._onForeground(hwnd);
        }
        catch (Exception ex)
        {
            self._logger.LogError(ex, "Foreground change handler failed");
        }
    }
}
