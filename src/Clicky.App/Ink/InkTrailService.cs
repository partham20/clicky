using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Core;
using Clicky.Core.Assistant;
using Clicky.Core.Geometry;
using Clicky.Core.Ink;
using Clicky.Core.Settings;
using Clicky.Platform.Windows.Capture;
using Clicky.Platform.Windows.Input;
using Clicky.Platform.Windows.Overlay;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Ink;

/// <summary>
/// Records the pointer while Talk is held, draws the trail on the overlays as it goes, and turns
/// it into spatial context for the request (see <see cref="IInkTrailService"/>).
/// </summary>
/// <remarks>
/// <para>Sampling runs on its own thread (<see cref="PointerSampler"/>), so a busy UI thread
/// costs frames of the drawing, never points of the trail.</para>
/// <para>Idle CPU: nothing runs between Talks. CompositionTarget.Rendering is hooked from the
/// start of a recording until its ink has faded, and only surfaces the trail touches redraw.</para>
/// <para>Overlays stay visible while ink is up, even with the floating buddy hidden.</para>
/// </remarks>
internal sealed class InkTrailService : IInkTrailService, IDisposable
{
    // Opacity steps for the fading tail: each step is one geometry per frame.
    private const int OpacitySteps = 12;

    private readonly Lazy<OverlayHost> _overlays;
    private readonly Dispatcher _dispatcher;
    private readonly ISettingsStore _settings;
    private readonly ClickyPaths _paths;
    private readonly ILogger<InkTrailService> _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Recording? _current;

    // UI thread only.
    private readonly List<InkTrailSurface> _surfaces = [];
    private readonly List<InkSample> _points = [];
    private Recording? _shown;
    private int _firstVisible;
    private IDisposable? _keepVisible;
    private bool _attached;
    private bool _rendering;
    private bool _disposed;
    private TimeSpan _lastFrameTime;

    public InkTrailService(Lazy<OverlayHost> overlays, Dispatcher dispatcher, ISettingsStore settings, ClickyPaths paths, ILogger<InkTrailService> logger)
    {
        _overlays = overlays;
        _dispatcher = dispatcher;
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    public IInkRecording Start()
    {
        var ink = _settings.Current.Ink;
        if (!ink.Enabled || _disposed)
        {
            return NoRecording.Instance;
        }

        var recording = new Recording(this, _clock.Elapsed);
        Interlocked.Exchange(ref _current, recording)?.Cancel();
        recording.Begin();
        if (ink.ShowTrail)
        {
            _dispatcher.InvokeAsync(() => ShowNow(recording));
        }

        return recording;
    }

    public async Task<RequestScreenshot> AttachAsync(RequestScreenshot screenshot, InkTrail trail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screenshot);
        ArgumentNullException.ThrowIfNull(trail);
        var settings = _settings.Current;
        if (!settings.Ink.Enabled || trail.IsEmpty)
        {
            return screenshot;
        }

        var geometry = screenshot.Geometry;
        var ink = await Task.Run(() => InkAnalyzer.Analyze(trail, geometry), cancellationToken).ConfigureAwait(false);
        if (ink is null)
        {
            _logger.LogDebug("Ink: {Samples} samples over {Seconds:0.0} s, nothing worth sending", trail.Samples.Count, trail.Duration.TotalSeconds);
            return screenshot;
        }

        var png = screenshot.Png;
        if (settings.Ink.DrawOnScreenshot && ink.Strokes.Count > 0)
        {
            try
            {
                var (pixels, width, height) = await PngPixels.DecodeAsync(png, cancellationToken).ConfigureAwait(false);
                if (width == geometry.ImageWidth && height == geometry.ImageHeight)
                {
                    InkPainter.Paint(pixels, width, height, ink.Strokes);
                    png = await PngPixels.EncodeAsync(pixels, width, height, cancellationToken).ConfigureAwait(false);
                    ink = ink with { DrawnOnScreenshot = true };
                }
                else
                {
                    _logger.LogWarning("Ink: screenshot is {Width}x{Height} but its geometry says {Geometry}; sending the description only", width, height, geometry);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Ink: could not draw the trail on the screenshot; sending the description only");
            }
        }

        _logger.LogInformation("Ink: {Samples} samples over {Seconds:0.0} s -> {Description}", trail.Samples.Count, trail.Duration.TotalSeconds, ink.Describe());
        if (settings.Debug.SaveCaptures && ink.DrawnOnScreenshot)
        {
            await SaveDebugCopyAsync(png).ConfigureAwait(false);
        }

        return screenshot with { Png = png, Ink = ink };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _current, null)?.Cancel();
        if (_dispatcher.HasShutdownStarted || !_dispatcher.CheckAccess())
        {
            return;
        }

        StopRendering();
        if (_attached)
        {
            _overlays.Value.OverlaysChanged -= OnOverlaysChanged;
        }

        _keepVisible?.Dispose();
        _keepVisible = null;
    }

    private async Task SaveDebugCopyAsync(byte[] png)
    {
        try
        {
            Directory.CreateDirectory(_paths.DebugCapturesDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(_paths.DebugCapturesDirectory, $"{stamp}-ink.png");
            for (var n = 1; File.Exists(path); n++)
            {
                path = Path.Combine(_paths.DebugCapturesDirectory, $"{stamp}-ink-{n}.png");
            }

            await File.WriteAllBytesAsync(path, png).ConfigureAwait(false);
            _logger.LogInformation("Saved debug ink capture {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save debug ink capture");
        }
    }

    private void ShowNow(Recording recording)
    {
        if (_disposed || recording.IsCancelled)
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

        _shown = recording;
        _points.Clear();
        _firstVisible = 0;
        overlays.ReassertTopmost();
        StartRendering();
    }

    /// <summary>Wipes the ink of <paramref name="recording"/> at once, if it is the one on screen.</summary>
    private void Wipe(Recording recording) => _dispatcher.InvokeAsync(() =>
    {
        if (ReferenceEquals(_shown, recording))
        {
            Hide();
        }
    });

    private void Hide()
    {
        _shown = null;
        _points.Clear();
        foreach (var surface in _surfaces)
        {
            surface.Show([]);
        }

        StopRendering();
        _keepVisible?.Dispose();
        _keepVisible = null;
    }

    private void OnOverlaysChanged(object? sender, EventArgs e)
    {
        _surfaces.Clear();
        AttachSurfaces();
    }

    private void AttachSurfaces()
    {
        foreach (var overlay in _overlays.Value.Overlays)
        {
            var size = overlay.SurfaceSize;
            var surface = new InkTrailSurface(overlay.Monitor) { Width = size.Width, Height = size.Height };

            // Under the buddy and Clicky's own marks.
            Panel.SetZIndex(surface, -2);
            overlay.Surface.Children.Add(surface);
            _surfaces.Add(surface);
        }
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
        RenderFrame();
    }

    private void RenderFrame()
    {
        var recording = _shown;
        if (_disposed || recording is null || recording.IsCancelled)
        {
            Hide();
            return;
        }

        recording.CopyNewSamples(_points);
        var now = _clock.Elapsed - recording.StartedAt;
        var stoppedAt = recording.StoppedAt;
        if (stoppedAt is { } stop && (_points.Count == 0 || now >= InkFade.GoneAt(_points[^1].Time, stop)))
        {
            Hide();
            return;
        }

        // Ink fades oldest first, so everything before the first visible point stays gone.
        while (_firstVisible < _points.Count - 1 && InkFade.Opacity(_points[_firstVisible + 1].Time, now, stoppedAt) <= 0)
        {
            _firstVisible++;
        }

        var chunks = BuildChunks(now, stoppedAt);
        foreach (var surface in _surfaces)
        {
            surface.Show(chunks);
        }
    }

    /// <summary>The visible trail as runs of segments sharing one opacity step; each segment fades with its newer end.</summary>
    private List<InkChunk> BuildChunks(TimeSpan now, TimeSpan? stoppedAt)
    {
        var chunks = new List<InkChunk>();
        List<Point>? run = null;
        var runOpacity = -1.0;
        for (var i = _firstVisible + 1; i < _points.Count; i++)
        {
            var opacity = Math.Ceiling(InkFade.Opacity(_points[i].Time, now, stoppedAt) * OpacitySteps) / OpacitySteps;
            if (opacity <= 0)
            {
                continue;
            }

            if (run is null || opacity != runOpacity)
            {
                Flush();
                run = [ToPoint(_points[i - 1])];
                runOpacity = opacity;
            }

            run.Add(ToPoint(_points[i]));
        }

        Flush();
        return chunks;

        void Flush()
        {
            if (run is { Count: >= 2 })
            {
                double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
                foreach (var p in run)
                {
                    left = Math.Min(left, p.X);
                    top = Math.Min(top, p.Y);
                    right = Math.Max(right, p.X);
                    bottom = Math.Max(bottom, p.Y);
                }

                chunks.Add(new InkChunk([.. run], runOpacity, new Rect(left, top, right - left, bottom - top)));
            }

            run = null;
        }

        static Point ToPoint(InkSample sample) => new(sample.Position.X, sample.Position.Y);
    }

    /// <summary>One Talk's recording. Samples arrive on the sampler thread; everything else may be on any thread.</summary>
    private sealed class Recording(InkTrailService owner, TimeSpan startedAt) : IInkRecording
    {
        private readonly Lock _gate = new();
        private readonly InkTrailBuilder _builder = new();
        private PointerSampler? _sampler;
        private InkTrail? _trail;
        private TimeSpan _lastSampleTime;
        private long _stoppedAtTicks = -1;
        private volatile bool _cancelled;

        /// <summary>On the service's clock.</summary>
        public TimeSpan StartedAt { get; } = startedAt;

        public bool IsCancelled => _cancelled;

        /// <summary>When recording stopped, from its start; null while it runs.</summary>
        public TimeSpan? StoppedAt
        {
            get
            {
                var ticks = Interlocked.Read(ref _stoppedAtTicks);
                return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
            }
        }

        public void Begin()
        {
            var sampler = PointerSampler.Start(OnSample, owner._logger);
            lock (_gate)
            {
                if (_trail is null && !_cancelled)
                {
                    _sampler = sampler;
                    return;
                }
            }

            // Finished or cancelled before it got going.
            sampler.Dispose();
        }

        public InkTrail Finish()
        {
            var sampler = Detach();
            if (sampler is not null)
            {
                // Outside the lock: the last sample may be waiting on it.
                sampler.Dispose();
            }

            lock (_gate)
            {
                if (_trail is null)
                {
                    // The sampler started its clock just after StartedAt; its last reading is close enough to now.
                    var duration = owner._clock.Elapsed - StartedAt;
                    if (duration < _lastSampleTime)
                    {
                        duration = _lastSampleTime;
                    }

                    _trail = _cancelled ? InkTrail.Empty : _builder.Build(duration);
                    Interlocked.Exchange(ref _stoppedAtTicks, duration.Ticks);
                }

                return _trail;
            }
        }

        public void Cancel()
        {
            _cancelled = true;
            Detach()?.Dispose();
            lock (_gate)
            {
                _trail ??= InkTrail.Empty;
                if (Interlocked.Read(ref _stoppedAtTicks) < 0)
                {
                    Interlocked.Exchange(ref _stoppedAtTicks, (owner._clock.Elapsed - StartedAt).Ticks);
                }
            }

            Interlocked.CompareExchange(ref owner._current, null, this);
            owner.Wipe(this);
        }

        public void Dispose() => Finish();

        /// <summary>Appends samples recorded since the last call. UI thread.</summary>
        public void CopyNewSamples(List<InkSample> into)
        {
            lock (_gate)
            {
                for (var i = into.Count; i < _builder.Count; i++)
                {
                    into.Add(_builder[i]);
                }
            }
        }

        private PointerSampler? Detach()
        {
            lock (_gate)
            {
                var sampler = _sampler;
                _sampler = null;
                return sampler;
            }
        }

        private void OnSample(PixelPoint position, TimeSpan time)
        {
            lock (_gate)
            {
                if (_trail is not null || _cancelled)
                {
                    return;
                }

                _lastSampleTime = time;
                _builder.Add(position, time);
            }
        }
    }

    private sealed class NoRecording : IInkRecording
    {
        public static readonly NoRecording Instance = new();

        public InkTrail Finish() => InkTrail.Empty;

        public void Cancel()
        {
        }

        public void Dispose()
        {
        }
    }
}
