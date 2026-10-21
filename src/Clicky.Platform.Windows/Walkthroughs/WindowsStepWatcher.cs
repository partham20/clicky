using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Clicky.Core.Actions;
using Clicky.Core.Geometry;
using Clicky.Core.Walkthroughs;
using Microsoft.Extensions.Logging;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Walkthroughs;

/// <summary>
/// Watches for the user doing a walkthrough step: a low-level mouse hook for clicks, and
/// accessibility (WinEvent) hooks for focus, selection, invoke, menus opening and the foreground
/// window changing, so keyboard users are seen too.
/// </summary>
/// <remarks>
/// <para>Two threads of its own, each with a message loop. The mouse hook's thread does nothing but
/// hand button events on: Windows calls a low-level hook for every mouse event system-wide and drops it
/// (silently) if a call takes longer than LowLevelHooksTimeout, and pointer movement stutters for
/// everyone while one is slow. Moves are passed straight on.</para>
/// <para>The second thread owns the WinEvent hooks and does the slow part: asking the app under a click
/// what element is there (MSAA, a cross-process call), and reading focused elements' bounds.</para>
/// <para>Clicks and events on Clicky's own windows (the step card) are flagged or dropped, except windows
/// registered with <see cref="Exempt"/> (the walkthrough demo).</para>
/// <para>UIPI: while an elevated window is in front, a non-elevated Clicky sees neither its clicks nor
/// its events; the card's Done button still works.</para>
/// </remarks>
public sealed class WindowsStepWatcher(ILogger<WindowsStepWatcher> logger) : IStepWatcher
{
    private static readonly ConcurrentDictionary<nint, byte> s_exempt = new();

    private readonly Lock _gate = new();
    private Watch? _active;

    /// <summary>
    /// Treats <paramref name="hwnd"/> (a top-level window of ours) like another app's window until the
    /// returned handle is disposed.
    /// </summary>
    public static IDisposable Exempt(nint hwnd)
    {
        s_exempt[hwnd] = 0;
        return new Exemption(hwnd);
    }

    public IDisposable Start(Action<StepObservation> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            if (_active is not null)
            {
                throw new InvalidOperationException("The step watcher is already running.");
            }

            var watch = new Watch(this, observer, logger);
            try
            {
                watch.Start();
            }
            catch
            {
                watch.Dispose();
                throw;
            }

            _active = watch;
            return watch;
        }
    }

    private void Stopped(Watch watch)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_active, watch))
            {
                _active = null;
            }
        }
    }

    private sealed class Exemption(nint hwnd) : IDisposable
    {
        public void Dispose() => s_exempt.TryRemove(hwnd, out _);
    }

    /// <summary>One run of the hooks.</summary>
    private sealed class Watch(WindowsStepWatcher owner, Action<StepObservation> observer, ILogger logger) : IDisposable
    {
        private const uint WmAppMouse = WmApp + 11;

        // Marks input Clicky injects (keyboard hotkeys, and the computer-use agent's clicks).
        private const nuint InjectionSignature = 0x434C_4B59; // "CLKY"

        private static Watch? s_current;

        private readonly ConcurrentQueue<MouseButtonEvent> _mouse = new();
        private readonly Dictionary<MouseButton, PendingDown> _down = [];
        private readonly List<nint> _winEventHooks = [];
        private readonly ManualResetEventSlim _workerReady = new();
        private readonly ManualResetEventSlim _mouseReady = new();
        private readonly uint _ownProcess = (uint)Environment.ProcessId;

        private Thread? _worker;
        private Thread? _mouseThread;
        private uint _workerThreadId;
        private uint _mouseThreadId;
        private nint _mouseHook;
        private int _mouseHookError;
        private int _disposed;

        public void Start()
        {
            if (Interlocked.CompareExchange(ref s_current, this, null) is not null)
            {
                throw new InvalidOperationException("Only one step watcher per process.");
            }

            _worker = new Thread(RunWorker) { Name = "Clicky walkthrough watcher", IsBackground = true };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
            _workerReady.Wait();

            _mouseThread = new Thread(RunMouse) { Name = "Clicky walkthrough mouse hook", IsBackground = true, Priority = ThreadPriority.Highest };
            _mouseThread.Start();
            _mouseReady.Wait();
            if (_mouseHook == 0)
            {
                throw new InvalidOperationException($"SetWindowsHookEx(WH_MOUSE_LL) failed with error {_mouseHookError}.");
            }

            logger.LogInformation("Walkthrough watcher on: mouse hook and {Count} accessibility hooks", _winEventHooks.Count);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Stop(_mouseThread, _mouseThreadId, "mouse hook");
            Stop(_worker, _workerThreadId, "watcher");
            Interlocked.CompareExchange(ref s_current, null, this);
            owner.Stopped(this);
            _workerReady.Dispose();
            _mouseReady.Dispose();
            logger.LogInformation("Walkthrough watcher off");
        }

        private void Stop(Thread? thread, uint threadId, string what)
        {
            if (thread is not { IsAlive: true })
            {
                return;
            }

            PostThreadMessage(threadId, WmQuit, 0, 0);
            if (!thread.Join(TimeSpan.FromSeconds(2)))
            {
                logger.LogWarning("Walkthrough {What} thread did not stop within 2 s", what);
            }
        }

        // ---- Mouse hook thread ----

        private unsafe void RunMouse()
        {
            _mouseThreadId = GetCurrentThreadId();
            PeekMessage(out _, 0, 0, 0, PmNoRemove);
            _mouseHook = SetWindowsHookEx(WhMouseLl, &MouseProc, GetModuleHandle(null), 0);
            _mouseHookError = _mouseHook == 0 ? Marshal.GetLastPInvokeError() : 0;
            _mouseReady.Set();
            if (_mouseHook == 0)
            {
                return;
            }

            try
            {
                while (GetMessage(out var msg, 0, 0, 0) > 0)
                {
                    DispatchMessage(msg);
                }
            }
            finally
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = 0;
            }
        }

        [UnmanagedCallersOnly]
        private static unsafe nint MouseProc(int code, nint wParam, nint lParam)
        {
            // Every mouse move in the system comes through here: only button messages do any work.
            var message = (uint)wParam;
            if (code >= 0 && message is WmLButtonDown or WmLButtonUp or WmRButtonDown or WmRButtonUp or WmMButtonDown or WmMButtonUp && s_current is { } self)
            {
                var info = (MsLlHookStruct*)lParam;
                if (info->ExtraInfo != InjectionSignature)
                {
                    var button = message switch
                    {
                        WmLButtonDown or WmLButtonUp => MouseButton.Left,
                        WmRButtonDown or WmRButtonUp => MouseButton.Right,
                        _ => MouseButton.Middle,
                    };
                    var isDown = message is WmLButtonDown or WmRButtonDown or WmMButtonDown;
                    self._mouse.Enqueue(new MouseButtonEvent(button, isDown, new PixelPoint(info->Pt.X, info->Pt.Y)));
                    PostThreadMessage(self._workerThreadId, WmAppMouse, 0, 0);
                }
            }

            return CallNextHookEx(0, code, wParam, lParam);
        }

        // ---- Watcher thread ----

        private unsafe void RunWorker()
        {
            _workerThreadId = GetCurrentThreadId();
            PeekMessage(out _, 0, 0, 0, PmNoRemove);
            Hook(EventSystemForeground, EventSystemForeground);
            Hook(EventSystemMenuPopupStart, EventSystemMenuPopupStart);
            Hook(EventObjectFocus, EventObjectSelection);
            Hook(EventObjectInvoked, EventObjectInvoked);
            _workerReady.Set();

            try
            {
                while (GetMessage(out var msg, 0, 0, 0) > 0)
                {
                    if (msg.Hwnd == 0 && msg.Message == WmAppMouse)
                    {
                        while (_mouse.TryDequeue(out var button))
                        {
                            Safely(() => OnMouseButton(button));
                        }

                        continue;
                    }

                    DispatchMessage(msg);
                }
            }
            finally
            {
                foreach (var hook in _winEventHooks)
                {
                    UnhookWinEvent(hook);
                }

                _winEventHooks.Clear();
            }
        }

        private unsafe void Hook(uint min, uint max)
        {
            var hook = SetWinEventHook(min, max, 0, &WinEventProc, 0, 0, WineventOutOfContext);
            if (hook == 0)
            {
                logger.LogWarning("SetWinEventHook(0x{Min:X4}-0x{Max:X4}) failed", min, max);
                return;
            }

            _winEventHooks.Add(hook);
        }

        [UnmanagedCallersOnly]
        private static void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
        {
            if (s_current is { } self)
            {
                self.Safely(() => self.OnWinEvent(eventType, hwnd, idObject, idChild));
            }
        }

        private void OnMouseButton(MouseButtonEvent e)
        {
            if (e.IsDown)
            {
                // Read what is under the pointer now, before the click changes the screen.
                var onClicky = IsOwnWindow(WindowFromPoint(new Point { X = e.At.X, Y = e.At.Y }));
                _down[e.Button] = new PendingDown(e.At, onClicky, onClicky ? null : ElementAt(e.At));
                return;
            }

            if (_down.Remove(e.Button, out var down))
            {
                Report(new PointerClicked(down.At, e.Button, down.Element, down.OnClicky));
            }
        }

        private void OnWinEvent(uint eventType, nint hwnd, int idObject, int idChild)
        {
            if (hwnd == 0)
            {
                return;
            }

            if (eventType == EventSystemForeground)
            {
                var own = IsOwnWindow(hwnd);
                Report(new ForegroundChanged(WindowsForegroundApp.Describe(hwnd), own));
                return;
            }

            if (idObject is ObjidCaret or ObjidCursor || IsOwnWindow(hwnd))
            {
                return;
            }

            var kind = eventType switch
            {
                EventObjectFocus => ElementEventKind.Focus,
                EventObjectSelection => ElementEventKind.Select,
                EventObjectInvoked => ElementEventKind.Invoke,
                EventSystemMenuPopupStart => ElementEventKind.MenuOpened,
                _ => (ElementEventKind?)null,
            };
            if (kind is null)
            {
                return;
            }

            if (AccessibleObjectFromEvent(hwnd, idObject, idChild, out var accessible, out var child) < 0 || accessible is null)
            {
                return;
            }

            try
            {
                if (Bounds(accessible, child ?? ChildidSelf) is { } bounds)
                {
                    Report(new ElementEvent(kind.Value, bounds, Name(accessible, child ?? ChildidSelf)));
                }
            }
            finally
            {
                Release(accessible);
            }
        }

        private static PixelRect? ElementAt(PixelPoint at)
        {
            if (AccessibleObjectFromPoint(new Point { X = at.X, Y = at.Y }, out var accessible, out var child) < 0 || accessible is null)
            {
                return null;
            }

            try
            {
                return Bounds(accessible, child ?? ChildidSelf);
            }
            finally
            {
                Release(accessible);
            }
        }

        private static PixelRect? Bounds(IAccessible accessible, object child)
        {
            try
            {
                accessible.accLocation(out var left, out var top, out var width, out var height, child);
                var rect = new PixelRect(left, top, left + width, top + height);
                return rect.IsEmpty ? null : rect;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or NotImplementedException)
            {
                return null;
            }
        }

        private static string? Name(IAccessible accessible, object child)
        {
            try
            {
                var name = accessible.get_accName(child)?.Trim();
                return string.IsNullOrEmpty(name) ? null : name.Length > 80 ? name[..80] : name;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or NotImplementedException)
            {
                return null;
            }
        }

        private static void Release(IAccessible accessible)
        {
            if (Marshal.IsComObject(accessible))
            {
                Marshal.FinalReleaseComObject(accessible);
            }
        }

        /// <summary>A top-level window of Clicky's own that isn't exempt (the step card, overlays, Settings).</summary>
        private bool IsOwnWindow(nint hwnd)
        {
            if (hwnd == 0)
            {
                return false;
            }

            var root = GetAncestor(hwnd, GaRoot);
            if (root == 0)
            {
                root = hwnd;
            }

            return GetWindowThreadProcessId(root, out var pid) != 0 && pid == _ownProcess && !s_exempt.ContainsKey(root);
        }

        private void Report(StepObservation observation)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                observer(observation);
            }
        }

        private void Safely(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // An exception escaping an UnmanagedCallersOnly method kills the process.
                logger.LogError(ex, "Walkthrough watcher callback failed");
            }
        }

        private readonly record struct MouseButtonEvent(MouseButton Button, bool IsDown, PixelPoint At);

        private readonly record struct PendingDown(PixelPoint At, bool OnClicky, PixelRect? Element);
    }
}
