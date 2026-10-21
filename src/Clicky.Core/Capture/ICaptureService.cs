using Clicky.Core.Geometry;

namespace Clicky.Core.Capture;

/// <summary>What a screenshot covers. Names are written to settings.json, so do not rename them.</summary>
public enum CaptureMode
{
    /// <summary>The monitor under the pointer (nearest one if the pointer is in a gap).</summary>
    MonitorUnderCursor,

    /// <summary>The foreground window; falls back to the monitor under the pointer if there is none.</summary>
    ForegroundWindow,

    /// <summary>Every monitor, composed into one image of the virtual desktop's bounding box.</summary>
    AllMonitors,
}

/// <summary>How a screenshot is encoded. Names are written to settings.json, so do not rename them.</summary>
public enum ScreenshotFormat
{
    Jpeg,
    Png,
}

/// <param name="Mode">What to capture.</param>
/// <param name="CursorPosition">
/// Pointer position to use for <see cref="CaptureMode.MonitorUnderCursor"/> and for debug crosshairs.
/// Pass the position at the moment the user asked (hotkey arm time); null reads it at capture time.
/// </param>
public readonly record struct CaptureTarget(CaptureMode Mode, PixelPoint? CursorPosition = null);

/// <param name="Image">The downscaled screenshot, encoded as settings.capture.format says (see <see cref="ImageMediaType"/>).</param>
/// <param name="Geometry">Maps between <paramref name="Image"/>'s pixels and the screen.</param>
/// <param name="Monitor">The captured monitor, or null for windows and all-monitor captures.</param>
/// <param name="Elapsed">Wall time from request to encoded image.</param>
public sealed record CaptureResult(byte[] Image, ScreenshotGeometry Geometry, MonitorInfo? Monitor, TimeSpan Elapsed)
{
    public string MediaType => ImageMediaType.Of(Image);
}

public interface ICaptureService
{
    /// <summary>
    /// Takes one screenshot, downscaled to the caps in settings.capture. Clicky's own overlays are
    /// excluded. Thread-safe; concurrent calls are serialized.
    /// </summary>
    /// <exception cref="CaptureException">Capture is unsupported or failed.</exception>
    Task<CaptureResult> CaptureAsync(CaptureTarget target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the devices and loads the codecs a capture needs, so the first real capture is not
    /// slower than the rest. Takes no screenshot. Safe to call more than once; never throws.
    /// </summary>
    Task WarmUpAsync(CancellationToken cancellationToken = default);
}

public sealed class CaptureException : Exception
{
    public CaptureException()
    {
    }

    public CaptureException(string message)
        : base(message)
    {
    }

    public CaptureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
