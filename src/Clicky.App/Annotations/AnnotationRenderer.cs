using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Core.Actions;
using Clicky.Core.Annotations;
using Clicky.Core.Geometry;
using Clicky.Platform.Windows.Input;
using Clicky.Platform.Windows.Overlay;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Annotations;

/// <summary>
/// Draws point, highlight, draw and step actions on the per-monitor overlays (see
/// <see cref="IAnnotationRenderer"/>). One <see cref="AnnotationSurface"/> per overlay draws the
/// marks that touch its monitor.
/// </summary>
/// <remarks>
/// <para>Idle CPU budget (&lt;2%): CompositionTarget.Rendering is hooked only while a mark is flying,
/// drawing in, popping its label or fading. While marks just sit there, the only thing pending is a
/// one-shot timer for the next fade, and only surfaces with something on them are redrawn.</para>
/// <para>While any mark is up, the overlays are kept visible even if the floating buddy is hidden.</para>
/// <para>Constructing it is safe on any thread: the overlay host (which creates windows) is only
/// touched on the UI thread, when the first mark is shown.</para>
/// </remarks>
internal sealed class AnnotationRenderer : IAnnotationRenderer, IDisposable
{
    private readonly Lazy<OverlayHost> _overlays;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<AnnotationRenderer> _logger;
    private readonly AnnotationBoard _board = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<AnnotationSurface> _surfaces = [];

    private DispatcherTimer? _wake;
    private IDisposable? _keepVisible;
    private bool _attached;
    private bool _rendering;
    private bool _disposed;
    private TimeSpan _lastFrameTime;
    private int _seed = Environment.TickCount;

    public AnnotationRenderer(Lazy<OverlayHost> overlays, Dispatcher dispatcher, ILogger<AnnotationRenderer> logger)
    {
        _overlays = overlays;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    private TimeSpan Now => _clock.Elapsed;

    public IAnnotation? Show(ClickyAction action, ScreenshotGeometry geometry, AnnotationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(geometry);
        if (!AnnotationPlanner.Draws(action))
        {
            return null;
        }

        var handle = new Handle(this);
        _dispatcher.InvokeAsync(() => ShowNow(handle, action, geometry, options ?? new AnnotationOptions()));
        return handle;
    }

    public void DismissAll() => _dispatcher.InvokeAsync(() =>
    {
        if (_disposed || _board.Entries.Count == 0)
        {
            return;
        }

        _board.DismissAll(Now);
        Pump();
    });

    public void Clear() => _dispatcher.InvokeAsync(() =>
    {
        if (_disposed || _board.Entries.Count == 0)
        {
            return;
        }

        _board.Clear();
        Pump();
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_dispatcher.HasShutdownStarted || !_dispatcher.CheckAccess())
        {
            // At exit WPF has torn the windows down already; nothing left to release.
            return;
        }

        StopRendering();
        _wake?.Stop();
        if (_attached)
        {
            _overlays.Value.OverlaysChanged -= OnOverlaysChanged;
        }

        _keepVisible?.Dispose();
        _keepVisible = null;
    }

    private void ShowNow(Handle handle, ClickyAction action, ScreenshotGeometry geometry, AnnotationOptions options)
    {
        if (_disposed)
        {
            return;
        }

        var overlays = _overlays.Value;
        if (!_attached)
        {
            _attached = true;
            overlays.OverlaysChanged += OnOverlaysChanged;
        }

        // Builds the overlays if the buddy never showed them; that raises OverlaysChanged.
        _keepVisible ??= overlays.KeepVisible();
        if (_surfaces.Count == 0 && overlays.Overlays.Count > 0)
        {
            AttachSurfaces();
        }

        var monitors = overlays.Monitors;
        double UnitAt(SketchPoint p) =>
            CoordinateMapper.NearestMonitor(monitors, new PixelPoint((int)Math.Round(p.X), (int)Math.Round(p.Y)))?.Scale ?? 1;

        if (!AnnotationPlanner.TryPlan(action, geometry, UnitAt, unchecked(++_seed), out var annotation, out var error) || annotation is null)
        {
            _logger.LogInformation("Not drawing {Action}: {Error}", action.Name, error);
            Pump();
            return;
        }

        var now = Now;
        SketchPoint? flyFrom = null;
        var flight = TimeSpan.Zero;
        if (annotation.Target is { } target)
        {
            flyFrom = options.PointerFrom is { } from
                ? new SketchPoint(from.X, from.Y)
                : _board.PointerPosition(now) ?? MousePosition();
            if (flyFrom is { } start)
            {
                flight = AnnotationTimeline.FlightDuration(start.DistanceTo(target) / annotation.Unit);
            }
        }

        var timeline = new AnnotationTimeline(
            now,
            flight,
            AnnotationTimeline.DrawDuration(annotation),
            options.Hold ?? AnnotationOptions.DefaultHold(annotation),
            AnnotationTimeline.DefaultFade,
            labelOnLanding: annotation.Kind == AnnotationKind.Point);
        var entry = _board.Add(annotation, timeline, flight > TimeSpan.Zero ? flyFrom : null, now);
        handle.Attach(entry.Id);

        var bounds = annotation.Bounds;
        _logger.LogDebug(
            "Drawing {Kind} #{Id} at ({Left:0},{Top:0})-({Right:0},{Bottom:0}) px, label {Label}",
            annotation.Kind,
            entry.Id,
            bounds.Left,
            bounds.Top,
            bounds.Right,
            bounds.Bottom,
            annotation.Label);

        // Waking up is the cheap moment to win back z-order from topmost windows opened meanwhile.
        overlays.ReassertTopmost();
        Pump();
    }

    private void Dismiss(long id)
    {
        if (!_disposed && _board.Dismiss(id, Now))
        {
            Pump();
        }
    }

    private static SketchPoint? MousePosition() =>
        CursorReader.TryGet(out var position, out _) ? new SketchPoint(position.X, position.Y) : null;

    private void OnOverlaysChanged(object? sender, EventArgs e)
    {
        _surfaces.Clear();
        AttachSurfaces();
        Pump();
    }

    private void AttachSurfaces()
    {
        var monitors = _overlays.Value.Monitors;
        foreach (var overlay in _overlays.Value.Overlays)
        {
            var size = overlay.SurfaceSize;
            var surface = new AnnotationSurface(overlay.Monitor, monitors, _board) { Width = size.Width, Height = size.Height };

            // Under the buddy, which the overlay may already hold.
            Panel.SetZIndex(surface, -1);
            overlay.Surface.Children.Add(surface);
            _surfaces.Add(surface);
        }
    }

    /// <summary>Brings the board up to now, redraws what changed and decides when to look again.</summary>
    private void Pump()
    {
        if (_disposed)
        {
            return;
        }

        var state = _board.Update(Now);
        foreach (var surface in _surfaces)
        {
            if (surface.HasInk || surface.Touches(_board.Entries))
            {
                surface.InvalidateVisual();
            }
        }

        if (state.IsEmpty)
        {
            StopRendering();
            _wake?.Stop();
            _keepVisible?.Dispose();
            _keepVisible = null;
            return;
        }

        if (state.IsAnimating)
        {
            _wake?.Stop();
            StartRendering();
            return;
        }

        StopRendering();
        if (state.NextChange is { } next)
        {
            _wake ??= CreateWakeTimer();
            _wake.Stop();
            _wake.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling((next - Now).TotalMilliseconds)));
            _wake.Start();
        }
    }

    private DispatcherTimer CreateWakeTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Pump();
        };
        return timer;
    }

    private void StartRendering()
    {
        if (_rendering)
        {
            return;
        }

        _rendering = true;
        _lastFrameTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
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

        _lastFrameTime = frameTime;
        Pump();
    }

    /// <summary>
    /// Handed out before the mark exists (it is planned on the UI thread), so a dismiss that arrives
    /// first is remembered and applied when it does.
    /// </summary>
    private sealed class Handle(AnnotationRenderer owner) : IAnnotation
    {
        private long _id;
        private bool _dismissRequested;

        public void Dismiss() => owner._dispatcher.InvokeAsync(() =>
        {
            if (_id == 0)
            {
                _dismissRequested = true;
            }
            else
            {
                owner.Dismiss(_id);
            }
        });

        public void Attach(long id)
        {
            _id = id;
            if (_dismissRequested)
            {
                owner.Dismiss(id);
            }
        }
    }
}
