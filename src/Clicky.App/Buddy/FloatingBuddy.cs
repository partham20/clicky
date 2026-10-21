using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Core.Buddy;
using Clicky.Core.Geometry;
using Clicky.Core.Settings;
using Clicky.Platform.Windows.Input;
using Clicky.Platform.Windows.Overlay;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Buddy;

/// <summary>
/// The buddy that follows the pointer: one visual per monitor overlay, eased toward the pointer in
/// physical pixels and drawn on whichever overlays it overlaps.
/// </summary>
/// <remarks>
/// <para>Idle CPU budget (&lt;2%): while the pointer is still, the only work is a ~30 Hz
/// GetCursorInfo poll. When it moves we hook CompositionTarget.Rendering for per-frame updates and
/// unhook once the buddy has settled, so WPF stops rendering frames.</para>
/// <para>Create, start and dispose on the UI thread.</para>
/// </remarks>
internal sealed class FloatingBuddy : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan TopmostReassertInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NominalFrame = TimeSpan.FromSeconds(1.0 / 60);

    // Frames with the buddy settled and the pointer still before we stop per-frame work.
    private const int StillFramesBeforePark = 10;

    // Room the caption bubble needs to the buddy's right before it flips to the left, in DIPs.
    private const double CaptionRoom = 340;

    private readonly OverlayHost _overlays;
    private readonly IBuddySkin _skin;
    private readonly IBuddyPresence _presence;
    private readonly ISettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<FloatingBuddy> _logger;
    private readonly DispatcherTimer _poll;
    private readonly FollowSmoother _smoother = new(TimeSpan.Zero);
    private readonly List<(OverlayWindow Overlay, IBuddyVisual Visual)> _visuals = [];

    private bool _started;
    private bool _floating;
    private bool _rendering;
    private bool _disposed;
    private TimeSpan _lastFrameTime;
    private int _stillFrames;
    private PixelPoint _pointer;
    private bool _pointerVisible;
    private long _lastTopmostTimestamp;
    private int _levelUpdatePending;

    public FloatingBuddy(
        OverlayHost overlays,
        IBuddySkin skin,
        IBuddyPresence presence,
        ISettingsStore settings,
        Dispatcher dispatcher,
        ILogger<FloatingBuddy> logger)
    {
        _overlays = overlays;
        _skin = skin;
        _presence = presence;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
        _poll = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = PollInterval };
        _poll.Tick += OnPoll;
    }

    public void Start()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _overlays.OverlaysChanged += OnOverlaysChanged;
        _presence.StateChanged += OnPresenceChanged;
        _presence.LevelChanged += OnLevelChanged;
        _presence.CaptionChanged += OnCaptionChanged;
        _settings.Changed += OnSettingsChanged;
        Apply(_settings.Current.Overlay);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _presence.StateChanged -= OnPresenceChanged;
        _presence.LevelChanged -= OnLevelChanged;
        _presence.CaptionChanged -= OnCaptionChanged;
        _overlays.OverlaysChanged -= OnOverlaysChanged;

        if (_dispatcher.HasShutdownStarted)
        {
            return;
        }

        _poll.Stop();
        StopRendering();
    }

    private void Apply(OverlaySettings settings)
    {
        _smoother.TimeConstant = TimeSpan.FromMilliseconds(Math.Clamp(settings.FollowSmoothingMs, 0, OverlaySettings.MaxFollowSmoothingMs));

        if (settings.HideFloatingCursor)
        {
            if (_floating)
            {
                _logger.LogInformation("Floating buddy hidden; docked to Home");
            }

            _floating = false;
            _poll.Stop();
            StopRendering();
            SetAllIdle();
            _overlays.Hide();
            return;
        }

        if (!_floating)
        {
            _logger.LogInformation("Floating buddy shown (smoothing {SmoothingMs} ms)", _smoother.TimeConstant.TotalMilliseconds);
        }

        _floating = true;
        _overlays.Show();
        _poll.Start();
        StartRendering();
    }

    private void OnOverlaysChanged(object? sender, EventArgs e)
    {
        _visuals.Clear();
        foreach (var overlay in _overlays.Overlays)
        {
            var visual = _skin.Create();
            visual.Element.Visibility = Visibility.Collapsed;
            overlay.Surface.Children.Add(visual.Element);
            _visuals.Add((overlay, visual));
        }

        if (_floating)
        {
            StartRendering();
        }
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        if (_rendering || !CursorReader.TryGet(out var pointer, out var visible))
        {
            return;
        }

        if (pointer != _pointer || visible != _pointerVisible)
        {
            StartRendering();
        }
    }

    private void StartRendering()
    {
        if (_rendering || !_floating)
        {
            return;
        }

        _rendering = true;
        _lastFrameTime = TimeSpan.Zero;
        _stillFrames = 0;
        CompositionTarget.Rendering += OnRendering;

        // Waking up is the cheap moment to win back z-order from topmost windows opened meanwhile.
        var now = Environment.TickCount64;
        if (now - _lastTopmostTimestamp >= TopmostReassertInterval.TotalMilliseconds)
        {
            _lastTopmostTimestamp = now;
            _overlays.ReassertTopmost();
        }
    }

    private void StopRendering()
    {
        if (!_rendering)
        {
            return;
        }

        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Rendering can fire more than once per frame; RenderingTime only advances once.
        var frameTime = e is RenderingEventArgs args ? args.RenderingTime : TimeSpan.Zero;
        if (frameTime == _lastFrameTime && frameTime != TimeSpan.Zero)
        {
            return;
        }

        var elapsed = _lastFrameTime == TimeSpan.Zero ? NominalFrame : frameTime - _lastFrameTime;
        _lastFrameTime = frameTime;

        var moved = false;
        if (CursorReader.TryGet(out var pointer, out var visible))
        {
            moved = pointer != _pointer || visible != _pointerVisible;
            _pointer = pointer;
            _pointerVisible = visible;
        }

        _smoother.Step(_pointer.X, _pointer.Y, elapsed);
        Place();

        _stillFrames = moved || !_smoother.IsSettled ? 0 : _stillFrames + 1;
        if (_stillFrames >= StillFramesBeforePark)
        {
            StopRendering();
        }
    }

    private void Place()
    {
        var state = _presence.State;
        var level = _presence.Level;
        var caption = _presence.Caption;
        foreach (var (overlay, visual) in _visuals)
        {
            var monitor = overlay.Monitor;
            var anchor = CoordinateMapper.ToMonitorDip(_smoother.X, _smoother.Y, monitor);
            var topLeft = CoordinateMapper.SnapToPixel(new DipPoint(anchor.X + visual.Offset.X, anchor.Y + visual.Offset.Y), monitor.Scale);

            // Near a monitor edge the buddy straddles two overlays; each draws its part.
            var surface = overlay.SurfaceSize;
            var onThisMonitor = _pointerVisible
                && topLeft.X < surface.Width
                && topLeft.Y < surface.Height
                && topLeft.X + visual.Extent.Width > 0
                && topLeft.Y + visual.Extent.Height > 0;

            if (onThisMonitor)
            {
                visual.MoveTo(topLeft);
                visual.State = state;
                visual.Level = level;
                visual.Caption = caption;
                visual.CaptionOnLeft = topLeft.X + visual.Extent.Width + CaptionRoom > surface.Width;
                visual.Element.Visibility = Visibility.Visible;
            }
            else if (visual.Element.Visibility != Visibility.Collapsed)
            {
                visual.Element.Visibility = Visibility.Collapsed;
                visual.State = BuddyState.Idle;
                visual.Caption = null;
            }
        }
    }

    private void SetAllIdle()
    {
        foreach (var (_, visual) in _visuals)
        {
            visual.State = BuddyState.Idle;
            visual.Element.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPresenceChanged(object? sender, BuddyStateChangedEventArgs e) => RequestPlace();

    private void OnCaptionChanged(object? sender, EventArgs e) => RequestPlace();

    private void RequestPlace() =>
        _dispatcher.InvokeAsync(() =>
        {
            if (_floating && !_disposed)
            {
                Place();
            }
        });

    // Up to ~20 a second while listening; at most one update is queued on the dispatcher at a time.
    private void OnLevelChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _levelUpdatePending, 1) != 0)
        {
            return;
        }

        _dispatcher.InvokeAsync(
            () =>
            {
                Volatile.Write(ref _levelUpdatePending, 0);
                if (!_floating || _disposed)
                {
                    return;
                }

                var level = _presence.Level;
                foreach (var (_, visual) in _visuals)
                {
                    if (visual.Element.Visibility == Visibility.Visible)
                    {
                        visual.Level = level;
                    }
                }
            },
            DispatcherPriority.Render);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Overlay != e.Current.Overlay)
        {
            _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    Apply(e.Current.Overlay);
                }
            });
        }
    }
}
