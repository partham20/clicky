using System.Diagnostics;
using System.Runtime.InteropServices;
using Clicky.Core.Geometry;
using Microsoft.Extensions.Logging;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Input;

/// <summary>
/// Reads the pointer position on a thread of its own at a steady rate (125 Hz by default) until
/// disposed, for the Talk ink trail. Each reading goes to the callback with the time since start;
/// the first is taken at once.
/// </summary>
/// <remarks>
/// <para>Polling rather than a WH_MOUSE_LL hook: it adds nothing to every mouse event system-wide,
/// keeps working while an elevated window has focus (UIPI hides that input from a medium-integrity
/// hook), and leaves the low-level mouse hook to walkthroughs (M8).</para>
/// <para>A high-resolution waitable timer keeps the interval near 8 ms without raising the system
/// timer rate; before Windows 10 1803 it falls back to a normal one (about 64 Hz).</para>
/// <para>The callback runs on the sampler thread; it must be quick and must not throw. Readings
/// are skipped while the position is unavailable (secure desktop).</para>
/// </remarks>
public sealed class PointerSampler : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(8);

    private readonly Action<PixelPoint, TimeSpan> _onSample;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private int _disposed;

    private PointerSampler(Action<PixelPoint, TimeSpan> onSample, TimeSpan interval, ILogger logger)
    {
        _onSample = onSample;
        _interval = interval;
        _logger = logger;
        _thread = new Thread(Run) { Name = "Clicky pointer sampler", IsBackground = true, Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Starts sampling. Dispose to stop.</summary>
    public static PointerSampler Start(Action<PixelPoint, TimeSpan> onSample, ILogger logger, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(onSample);
        ArgumentNullException.ThrowIfNull(logger);
        var sampler = new PointerSampler(onSample, interval ?? DefaultInterval, logger);
        sampler._thread.Start();
        return sampler;
    }

    /// <summary>Stops sampling. No callback runs after this returns, unless called from the callback itself.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping = true;
        if (Thread.CurrentThread != _thread)
        {
            // At most one interval away.
            _thread.Join(TimeSpan.FromMilliseconds(250));
        }
    }

    private void Run()
    {
        var timer = CreateWaitableTimerEx(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (timer == 0)
        {
            timer = CreateWaitableTimerEx(0, null, 0, TimerAllAccess);
        }

        if (timer == 0)
        {
            _logger.LogWarning("Pointer sampler: no waitable timer (error {Error}); sampling with Sleep", Marshal.GetLastPInvokeError());
        }

        try
        {
            var clock = Stopwatch.StartNew();
            var due = -_interval.Ticks;
            while (!_stopping)
            {
                if (CursorReader.TryGet(out var position, out _))
                {
                    _onSample(position, clock.Elapsed);
                }

                if (timer == 0 || !SetWaitableTimer(timer, in due, 0, 0, 0, false) || WaitForSingleObject(timer, Infinite) != WaitObject0)
                {
                    Thread.Sleep(_interval);
                }
            }
        }
        catch (Exception ex)
        {
            // A background thread's exception would take the process down; losing the trail is enough.
            _logger.LogError(ex, "Pointer sampler failed");
        }
        finally
        {
            if (timer != 0)
            {
                CloseHandle(timer);
            }
        }
    }
}
