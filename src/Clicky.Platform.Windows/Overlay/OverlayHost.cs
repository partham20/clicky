using System.Windows.Threading;
using Clicky.Core.Geometry;
using Clicky.Platform.Windows.Display;
using Microsoft.Extensions.Logging;

namespace Clicky.Platform.Windows.Overlay;

/// <summary>
/// Owns one <see cref="OverlayWindow"/> per monitor and rebuilds the set when the display topology
/// or any monitor's scale changes. Create and use on the UI thread.
/// </summary>
public sealed class OverlayHost : IDisposable
{
    private static readonly TimeSpan DisplayDebounce = TimeSpan.FromMilliseconds(300);

    private readonly Dispatcher _dispatcher;
    private readonly ILogger<OverlayHost> _logger;
    private readonly DisplayChangeWatcher _watcher;

    private OverlayWindow[] _overlays = [];
    private bool _visible;
    private bool _shown;
    private int _keepVisibleCount;
    private bool _disposed;

    public OverlayHost(Dispatcher dispatcher, ILogger<OverlayHost> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _watcher = new DisplayChangeWatcher(dispatcher, DisplayDebounce);
        _watcher.Changed += OnDisplaysChanged;
    }

    /// <summary>Raised on the UI thread after the overlay set is replaced. Old windows are already closed.</summary>
    public event EventHandler? OverlaysChanged;

    public IReadOnlyList<OverlayWindow> Overlays => _overlays;

    public IReadOnlyList<MonitorInfo> Monitors => [.. _overlays.Select(o => o.Monitor)];

    public bool IsVisible => _visible;

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcher.VerifyAccess();
        _shown = true;
        ApplyVisibility();
    }

    /// <summary>Hides the overlays, unless something holds <see cref="KeepVisible"/>.</summary>
    public void Hide()
    {
        _dispatcher.VerifyAccess();
        _shown = false;
        ApplyVisibility();
    }

    /// <summary>
    /// Keeps the overlays on screen until the returned lease is disposed, whatever Show and Hide say,
    /// so marks drawn on them stay visible while the floating buddy is hidden. UI thread only.
    /// </summary>
    public IDisposable KeepVisible()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcher.VerifyAccess();
        if (_keepVisibleCount++ == 0 && !_shown)
        {
            ApplyVisibility();
        }

        return new VisibilityLease(this);
    }

    public void ReassertTopmost()
    {
        foreach (var overlay in _overlays)
        {
            overlay.ReassertTopmost();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // After Application.Shutdown the dispatcher is gone and WPF has closed every window already.
        if (_dispatcher.HasShutdownStarted)
        {
            return;
        }

        _watcher.Changed -= OnDisplaysChanged;
        _watcher.Dispose();
        CloseAll();
    }

    private void ApplyVisibility()
    {
        _visible = _shown || _keepVisibleCount > 0;
        if (!_visible)
        {
            foreach (var overlay in _overlays.Where(o => !o.IsClosed))
            {
                overlay.Hide();
            }

            return;
        }

        if (_overlays.Length == 0)
        {
            Rebuild(DisplayMonitors.GetAll());
            return;
        }

        foreach (var overlay in _overlays.Where(o => !o.IsClosed))
        {
            overlay.Show();
        }
    }

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var monitors = DisplayMonitors.GetAll();
        if (monitors.SequenceEqual(_overlays.Select(o => o.Monitor)))
        {
            return;
        }

        if (!_visible)
        {
            // Rebuilt from fresh monitor info on the next Show.
            _logger.LogInformation("Displays changed while overlays are hidden; dropping them");
            CloseAll();
            OverlaysChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        Rebuild(monitors);
    }

    private void Rebuild(IReadOnlyList<MonitorInfo> monitors)
    {
        _logger.LogInformation("Building overlays for {Count} monitor(s): {Monitors}", monitors.Count, string.Join("; ", monitors));
        CloseAll();

        _overlays = [.. monitors.Select(m => new OverlayWindow(m, _logger))];
        foreach (var overlay in _overlays)
        {
            overlay.DpiChanged += OnOverlayDpiChanged;
            if (_visible)
            {
                overlay.Show();
            }
        }

        OverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    // A scale change on one monitor does not always come with WM_DISPLAYCHANGE.
    private void OnOverlayDpiChanged(object? sender, System.Windows.DpiChangedEventArgs e) => _watcher.Poke();

    private void CloseAll()
    {
        foreach (var overlay in _overlays)
        {
            overlay.DpiChanged -= OnOverlayDpiChanged;
            if (!overlay.IsClosed)
            {
                overlay.Close();
            }
        }

        _overlays = [];
    }

    private sealed class VisibilityLease(OverlayHost host) : IDisposable
    {
        private OverlayHost? _host = host;

        public void Dispose()
        {
            var host = Interlocked.Exchange(ref _host, null);
            if (host is null || host._disposed || host._dispatcher.HasShutdownStarted)
            {
                return;
            }

            host._dispatcher.VerifyAccess();
            if (--host._keepVisibleCount == 0 && !host._shown)
            {
                host.ApplyVisibility();
            }
        }
    }
}
