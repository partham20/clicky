using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Clicky.Core;
using Clicky.Core.Capture;
using Clicky.Core.Geometry;
using Clicky.Core.Settings;
using Clicky.Platform.Windows.Display;
using Clicky.Platform.Windows.Input;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Capture;

/// <summary>
/// One-shot screenshots with Windows.Graphics.Capture: open a session, take the first frame, close
/// it. Overlays with WDA_EXCLUDEFROMCAPTURE (the buddy) are left out by the compositor.
/// </summary>
/// <remarks>
/// <para>Frames are BGRA8. On an HDR monitor the compositor tone-maps to SDR, which can look washed
/// out; the format is logged so such reports can be matched up.</para>
/// <para>Calls are serialized: the D3D device is shared, and a second capture of the same monitor
/// while one is in flight would gain nothing.</para>
/// </remarks>
public sealed class WindowsCaptureService : ICaptureService, IDisposable
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(2);
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly ISettingsStore _settings;
    private readonly ClickyPaths _paths;
    private readonly ILogger<WindowsCaptureService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IDirect3DDevice? _device;
    private Task<bool>? _borderless;
    private bool _disposed;

    public WindowsCaptureService(ISettingsStore settings, ClickyPaths paths, ILogger<WindowsCaptureService> logger)
    {
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    public async Task<CaptureResult> CaptureAsync(CaptureTarget target, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new CaptureException("Windows.Graphics.Capture is not supported on this system.");
        }

        var stopwatch = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _settings.Current;
            var cursor = target.CursorPosition ?? (CursorReader.TryGet(out var position, out _) ? position : default);
            _borderless ??= RequestBorderlessAsync();
            var borderless = await _borderless.ConfigureAwait(false);

            Frame frame;
            MonitorInfo? monitor = null;
            try
            {
                (frame, monitor) = await CaptureFrameAsync(target.Mode, cursor, settings.Capture.IncludeCursor, borderless, cancellationToken).ConfigureAwait(false);
            }
            catch (COMException ex)
            {
                // Typically DXGI_ERROR_DEVICE_REMOVED after a driver update or GPU switch: start over once.
                _logger.LogWarning(ex, "Capture failed (0x{HResult:X8}); recreating the D3D device and retrying", ex.HResult);
                _device?.Dispose();
                _device = null;
                (frame, monitor) = await CaptureFrameAsync(target.Mode, cursor, settings.Capture.IncludeCursor, borderless, cancellationToken).ConfigureAwait(false);
            }

            var (width, height) = CoordinateMapper.DownscaleSize(frame.Width, frame.Height, settings.Capture.MaxLongEdge, settings.Capture.MaxPixels);
            var png = await EncodePngAsync(frame.Pixels, frame.Width, frame.Height, width, height).ConfigureAwait(false);
            var geometry = new ScreenshotGeometry(frame.Source, width, height);
            stopwatch.Stop();

            if (settings.Debug.SaveCaptures)
            {
                await SaveDebugCopyAsync(png, geometry, cursor).ConfigureAwait(false);
            }

            return new CaptureResult(png, geometry, monitor, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            throw new CaptureException($"Screen capture failed: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || !GraphicsCaptureSession.IsSupported())
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            _device ??= CreateDevice();
            _borderless ??= RequestBorderlessAsync();
            await _borderless.ConfigureAwait(false);

            // Loads the capture item factory and the PNG codec with its scaler.
            _ = ActivationFactoryWarmUp();
            await EncodePngAsync(new byte[16 * 16 * 4], 16, 16, 8, 8).ConfigureAwait(false);
            _logger.LogInformation("Capture path warmed up in {Ms:0} ms", stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "Capture warm-up failed; the first capture will be slower");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _device?.Dispose();
    }

    private async Task<(Frame Frame, MonitorInfo? Monitor)> CaptureFrameAsync(
        CaptureMode mode,
        PixelPoint cursor,
        bool includeCursor,
        bool borderless,
        CancellationToken cancellationToken)
    {
        var monitors = DisplayMonitors.GetAllWithHandles();
        if (monitors.Count == 0)
        {
            throw new CaptureException("No monitors found.");
        }

        if (mode == CaptureMode.AllMonitors)
        {
            return (await CaptureAllMonitorsAsync(monitors, includeCursor, borderless, cancellationToken).ConfigureAwait(false), null);
        }

        if (mode == CaptureMode.ForegroundWindow)
        {
            var window = await TryCaptureForegroundWindowAsync(includeCursor, borderless, cancellationToken).ConfigureAwait(false);
            if (window is not null)
            {
                return (window, null);
            }
        }

        var under = CoordinateMapper.NearestMonitor([.. monitors.Select(m => m.Info)], cursor)!;
        var handle = monitors.First(m => m.Info == under).Handle;
        var frame = await CaptureItemAsync(CaptureInterop.CreateItemForMonitor(handle), under.Bounds, includeCursor, borderless, cancellationToken).ConfigureAwait(false);
        return (frame, under);
    }

    private async Task<Frame?> TryCaptureForegroundWindowAsync(bool includeCursor, bool borderless, CancellationToken cancellationToken)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || IsIconic(hwnd) || !TryGetWindowBounds(hwnd, out var bounds))
        {
            _logger.LogDebug("No capturable foreground window; capturing the monitor under the pointer");
            return null;
        }

        try
        {
            return await CaptureItemAsync(CaptureInterop.CreateItemForWindow(hwnd), bounds, includeCursor, borderless, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or TimeoutException)
        {
            // The shell's own windows (desktop, taskbar) and some protected windows refuse window capture.
            _logger.LogInformation(ex, "Could not capture the foreground window \"{Title}\"; capturing the monitor under the pointer", ProcessElevation.GetWindowTitle(hwnd));
            return null;
        }
    }

    private async Task<Frame> CaptureAllMonitorsAsync(
        IReadOnlyList<(nint Handle, MonitorInfo Info)> monitors,
        bool includeCursor,
        bool borderless,
        CancellationToken cancellationToken)
    {
        var box = monitors.Select(m => m.Info.Bounds).Aggregate((a, b) =>
            new PixelRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)));

        // Zero-filled, so gaps between monitors of different sizes come out black.
        var canvas = new byte[checked(box.Width * box.Height * 4)];
        foreach (var (handle, info) in monitors)
        {
            var frame = await CaptureItemAsync(CaptureInterop.CreateItemForMonitor(handle), info.Bounds, includeCursor, borderless, cancellationToken).ConfigureAwait(false);
            var rowBytes = frame.Width * 4;
            for (var y = 0; y < frame.Height; y++)
            {
                var target = (((frame.Source.Top - box.Top + y) * box.Width) + (frame.Source.Left - box.Left)) * 4;
                System.Buffer.BlockCopy(frame.Pixels, y * rowBytes, canvas, target, rowBytes);
            }
        }

        return new Frame(canvas, box.Width, box.Height, box);
    }

    private async Task<Frame> CaptureItemAsync(GraphicsCaptureItem item, PixelRect expected, bool includeCursor, bool borderless, CancellationToken cancellationToken)
    {
        _device ??= CreateDevice();

        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, PixelFormat, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = includeCursor;
        if (borderless && !CaptureInterop.TrySetBorderRequired(session, required: false))
        {
            _logger.LogDebug("IsBorderRequired is not available; the capture border may flash");
        }

        var arrived = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (sender, _) =>
        {
            var next = sender.TryGetNextFrame();
            if (next is not null && !arrived.TrySetResult(next))
            {
                next.Dispose();
            }
        };

        session.StartCapture();
        using var frame = await arrived.Task.WaitAsync(FrameTimeout, cancellationToken).ConfigureAwait(false);
        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);

        var stride = bitmap.PixelWidth * 4;
        var full = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyToBuffer(full.AsBuffer());

        // A window's content can be smaller than the pool (it shrank after we sized it); the rest is junk.
        var width = Math.Min(frame.ContentSize.Width, bitmap.PixelWidth);
        var height = Math.Min(frame.ContentSize.Height, bitmap.PixelHeight);
        var pixels = width == bitmap.PixelWidth ? full.AsSpan(0, stride * height).ToArray() : Crop(full, stride, width, height);

        if (width != expected.Width || height != expected.Height)
        {
            _logger.LogDebug("Captured {Width}x{Height}, expected {ExpectedWidth}x{ExpectedHeight} for {Item}", width, height, expected.Width, expected.Height, item.DisplayName);
        }

        _logger.LogDebug("Frame {Width}x{Height} {Format} from {Item}", width, height, PixelFormat, item.DisplayName);
        return new Frame(pixels, width, height, new PixelRect(expected.Left, expected.Top, expected.Left + width, expected.Top + height));
    }

    private static bool ActivationFactoryWarmUp() => WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem") is not null;

    private IDirect3DDevice CreateDevice()
    {
        var device = CaptureInterop.CreateDevice(out var description);
        _logger.LogInformation("Capture device ready ({Device}, {Format})", description, PixelFormat);
        return device;
    }

    private async Task<bool> RequestBorderlessAsync()
    {
        var allowed = await CaptureInterop.RequestBorderlessAccessAsync(CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Borderless capture {State}", allowed ? "allowed" : "not available (the yellow capture border may flash)");
        return allowed;
    }

    private static byte[] Crop(byte[] source, int stride, int width, int height)
    {
        var rowBytes = width * 4;
        var pixels = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            System.Buffer.BlockCopy(source, y * stride, pixels, y * rowBytes, rowBytes);
        }

        return pixels;
    }

    private static async Task<byte[]> EncodePngAsync(byte[] bgra, int width, int height, int outputWidth, int outputHeight)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        if (outputWidth != width || outputHeight != height)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)outputWidth;
            encoder.BitmapTransform.ScaledHeight = (uint)outputHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        await encoder.FlushAsync();

        var png = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(png.AsBuffer(), (uint)png.Length, InputStreamOptions.None);
        return png;
    }

    /// <summary>Writes the PNG with a red crosshair at the pointer's mapped screenshot position.</summary>
    private async Task SaveDebugCopyAsync(byte[] png, ScreenshotGeometry geometry, PixelPoint cursor)
    {
        try
        {
            byte[] pixels;
            using (var input = new InMemoryRandomAccessStream())
            {
                await input.WriteAsync(png.AsBuffer());
                input.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(input);
                var data = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Ignore,
                    new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                pixels = data.DetachPixelData();
            }

            var point = CoordinateMapper.ToScreenshot(cursor, geometry);
            DrawCrosshair(pixels, geometry.ImageWidth, geometry.ImageHeight, (int)Math.Round(point.X), (int)Math.Round(point.Y));
            var marked = await EncodePngAsync(pixels, geometry.ImageWidth, geometry.ImageHeight, geometry.ImageWidth, geometry.ImageHeight).ConfigureAwait(false);

            Directory.CreateDirectory(_paths.DebugCapturesDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(_paths.DebugCapturesDirectory, $"{stamp}.png");
            for (var n = 1; File.Exists(path); n++)
            {
                path = Path.Combine(_paths.DebugCapturesDirectory, $"{stamp}-{n}.png");
            }

            await File.WriteAllBytesAsync(path, marked).ConfigureAwait(false);
            _logger.LogInformation("Saved debug capture {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            _logger.LogWarning(ex, "Could not save debug capture");
        }
    }

    private static void DrawCrosshair(byte[] bgra, int width, int height, int cx, int cy)
    {
        const int arm = 12;
        const int gap = 3;
        for (var d = -arm; d <= arm; d++)
        {
            if (Math.Abs(d) < gap)
            {
                continue;
            }

            for (var t = -1; t <= 0; t++)
            {
                SetRed(bgra, width, height, cx + d, cy + t);
                SetRed(bgra, width, height, cx + t, cy + d);
            }
        }

        SetRed(bgra, width, height, cx, cy);
    }

    private static void SetRed(byte[] bgra, int width, int height, int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        var i = ((y * width) + x) * 4;
        bgra[i] = 0;
        bgra[i + 1] = 0;
        bgra[i + 2] = 255;
        bgra[i + 3] = 255;
    }

    /// <param name="Pixels">Tightly packed BGRA8 rows.</param>
    private sealed record Frame(byte[] Pixels, int Width, int Height, PixelRect Source);

    private static bool TryGetWindowBounds(nint hwnd, out PixelRect bounds)
    {
        // The visible frame, without the invisible resize borders GetWindowRect includes. Physical
        // pixels because the process is PerMonitorV2.
        Rect rect;
        unsafe
        {
            if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, &rect, sizeof(Rect)) < 0)
            {
                bounds = default;
                return false;
            }
        }

        bounds = new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return !bounds.IsEmpty;
    }
}
