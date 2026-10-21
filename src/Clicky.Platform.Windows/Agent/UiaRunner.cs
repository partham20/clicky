using FlaUI.UIA3;

namespace Clicky.Platform.Windows.Agent;

/// <summary>
/// Runs UI Automation calls off the UI thread, each on its own short-lived MTA thread, with a
/// deadline the caller doesn't wait past.
/// </summary>
/// <remarks>
/// Gotcha: UIA calls are cross-process and synchronous. An app that hangs blocks the call until
/// UIA's own timeout, and Invoke on a button that opens a modal dialog often doesn't return until
/// the dialog closes (the Win32 proxy sends BM_CLICK and waits). A thread per call lets the agent
/// move on and leaves the stuck call to finish by itself; the agent's pace (a few calls a second)
/// makes the threads cheap. CUIAutomation is free-threaded, so one instance serves every thread.
/// </remarks>
internal sealed class UiaRunner : IDisposable
{
    private readonly Lock _lock = new();
    private UIA3Automation? _automation;
    private bool _disposed;

    /// <summary>Runs <paramref name="work"/> and waits up to <paramref name="timeout"/>.</summary>
    /// <exception cref="TimeoutException">The call is still running (it keeps going in the background).</exception>
    public Task<T> RunAsync<T>(Func<UIA3Automation, T> work, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.TrySetResult(work(Automation()));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Clicky UI Automation",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return done.Task.WaitAsync(timeout, cancellationToken);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _automation?.Dispose();
            _automation = null;
        }
    }

    private UIA3Automation Automation()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _automation ??= new UIA3Automation
            {
                // Fail fast on a hung app instead of UIA's 20 s default; the agent reports it and moves on.
                ConnectionTimeout = TimeSpan.FromSeconds(2),
                TransactionTimeout = TimeSpan.FromSeconds(3),
            };
        }
    }
}
