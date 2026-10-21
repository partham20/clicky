namespace Clicky.Core.Geometry;

/// <summary>
/// Maps between physical screen pixels and the DIP space of a per-monitor overlay, whose origin is
/// the monitor's top-left and where 1 DIP = <see cref="MonitorInfo.Scale"/> physical pixels.
/// </summary>
/// <remarks>
/// Every overlay covers exactly one monitor, so each conversion uses that one monitor's scale. The
/// screenshot-pixel leg (capture downscale) is added in M2.
/// </remarks>
public static class CoordinateMapper
{
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
