namespace Clicky.Core.Geometry;

/// <summary>
/// Maps between physical screen pixels, the DIP space of a per-monitor overlay (origin at the
/// monitor's top-left, 1 DIP = <see cref="MonitorInfo.Scale"/> physical pixels), and the pixels of a
/// downscaled screenshot (<see cref="ScreenshotGeometry"/>).
/// </summary>
/// <remarks>
/// Every overlay covers exactly one monitor, so each DIP conversion uses that one monitor's scale.
/// Screenshot conversions use the capture's own scale, which is unrelated to the DPI setting.
/// </remarks>
public static class CoordinateMapper
{
    /// <summary>
    /// Where the centre of physical pixel <paramref name="point"/> lands in the screenshot, in
    /// screenshot pixels with integers at pixel centres. Points outside the source extrapolate.
    /// </summary>
    public static ScreenshotPoint ToScreenshot(PixelPoint point, ScreenshotGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return new ScreenshotPoint(
            ((point.X - geometry.Source.Left + 0.5) * geometry.ScaleX) - 0.5,
            ((point.Y - geometry.Source.Top + 0.5) * geometry.ScaleY) - 0.5);
    }

    /// <summary>
    /// The physical pixel under a screenshot position, clamped to the captured area. For an integer
    /// screenshot pixel that is the physical pixel at the centre of the region it was downscaled
    /// from. For any physical pixel p in the source, <c>ToPhysical(ToScreenshot(p, g), g) == p</c>.
    /// </summary>
    public static PixelPoint ToPhysical(ScreenshotPoint point, ScreenshotGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var source = geometry.Source;
        return new PixelPoint(
            source.Left + ToPhysicalOffset(point.X, geometry.ScaleX, source.Width),
            source.Top + ToPhysicalOffset(point.Y, geometry.ScaleY, source.Height));
    }

    /// <summary>
    /// The largest size with the same aspect ratio that fits both caps (long edge and total pixels).
    /// Never upscales, never returns less than 1x1.
    /// </summary>
    public static (int Width, int Height) DownscaleSize(int width, int height, int maxLongEdge, int maxPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLongEdge);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPixels);

        var scale = Math.Min(1.0, (double)maxLongEdge / Math.Max(width, height));
        scale = Math.Min(scale, Math.Sqrt((double)maxPixels / ((double)width * height)));
        if (scale >= 1.0)
        {
            return (width, height);
        }

        var w = Math.Clamp((int)Math.Floor(width * scale), 1, width);
        var h = Math.Clamp((int)Math.Floor(height * scale), 1, height);

        // Floating-point slack can leave the product a hair over the cap; shave the longer side.
        while ((long)w * h > maxPixels && (w > 1 || h > 1))
        {
            if (w >= h)
            {
                w--;
            }
            else
            {
                h--;
            }
        }

        return (w, h);
    }

    // Measured from the source edge, screenshot pixel u covers physical [u / s, (u + 1) / s), whose
    // centre is (u + 0.5) / s; we return the physical pixel containing that position. For
    // u = (p + 0.5) * s - 0.5 it is p + 0.5 (give or take a few ulps), half a pixel from either
    // rounding boundary, which is why the round trip is exact.
    private static int ToPhysicalOffset(double screenshot, double scale, int extent)
    {
        var physical = Math.Floor((screenshot + 0.5) / scale);
        if (double.IsNaN(physical))
        {
            return 0;
        }

        return (int)Math.Clamp(physical, 0, extent - 1);
    }

    public static DipPoint ToMonitorDip(PixelPoint point, MonitorInfo monitor) =>
        ToMonitorDip(point.X, point.Y, monitor);

    /// <summary>Fractional overload for smoothed positions that sit between pixels.</summary>
    public static DipPoint ToMonitorDip(double x, double y, MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return new DipPoint((x - monitor.Bounds.Left) / monitor.Scale, (y - monitor.Bounds.Top) / monitor.Scale);
    }

    /// <summary>
    /// Nearest physical pixel. For any integer pixel p, <c>ToPhysical(ToMonitorDip(p, m), m) == p</c>
    /// at every scale: the DIP value carries at most a few ulps of error, far below the 0.5 px rounding margin.
    /// </summary>
    public static PixelPoint ToPhysical(DipPoint dip, MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return new PixelPoint(
            monitor.Bounds.Left + (int)Math.Round(dip.X * monitor.Scale, MidpointRounding.AwayFromZero),
            monitor.Bounds.Top + (int)Math.Round(dip.Y * monitor.Scale, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Moves a DIP position onto the physical pixel grid. Placing vector art at a fractional pixel
    /// makes its edges shimmer as it moves; snapping keeps them crisp.
    /// </summary>
    public static DipPoint SnapToPixel(DipPoint dip, double scale) =>
        new(Math.Round(dip.X * scale, MidpointRounding.AwayFromZero) / scale, Math.Round(dip.Y * scale, MidpointRounding.AwayFromZero) / scale);

    /// <summary>The monitor containing <paramref name="point"/>, or null if it falls in a gap between monitors.</summary>
    public static MonitorInfo? MonitorAt(IReadOnlyList<MonitorInfo> monitors, PixelPoint point)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        foreach (var monitor in monitors)
        {
            if (monitor.Bounds.Contains(point))
            {
                return monitor;
            }
        }

        return null;
    }

    /// <summary>Same rule as MonitorFromPoint(MONITOR_DEFAULTTONEAREST); null only when there are no monitors.</summary>
    public static MonitorInfo? NearestMonitor(IReadOnlyList<MonitorInfo> monitors, PixelPoint point)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        MonitorInfo? best = null;
        var bestDistance = long.MaxValue;
        foreach (var monitor in monitors)
        {
            var distance = monitor.Bounds.DistanceSquaredTo(point);
            if (distance < bestDistance)
            {
                best = monitor;
                bestDistance = distance;
            }
        }

        return best;
    }
}
