using Clicky.Core.Geometry;

namespace Clicky.Tests.Geometry;

public sealed class CoordinateMapperTests
{
    public static TheoryData<double, int, int> ScalesAndOrigins()
    {
        var data = new TheoryData<double, int, int>();
        double[] scales = [1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 3.0, 3.5];

        // Primary, right of primary, left of primary, above primary, and an off-grid layout.
        (int X, int Y)[] origins = [(0, 0), (1920, 0), (-2560, 0), (0, -1440), (-2560, -360), (3840, 217)];
        foreach (var scale in scales)
        {
            foreach (var (x, y) in origins)
            {
                data.Add(scale, x, y);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScalesAndOrigins))]
    public void Every_sampled_pixel_round_trips_exactly(double scale, int originX, int originY)
    {
        var monitor = Monitor(originX, originY, 3840, 2160, scale);

        foreach (var pixel in Samples(monitor.Bounds))
        {
            var dip = CoordinateMapper.ToMonitorDip(pixel, monitor);
            Assert.Equal(pixel, CoordinateMapper.ToPhysical(dip, monitor));
        }
    }

    [Theory]
    [InlineData(1.0, 150, 300, 150, 300)]
    [InlineData(1.25, 150, 300, 120, 240)]
    [InlineData(1.5, 150, 300, 100, 200)]
    [InlineData(2.0, 150, 300, 75, 150)]
    public void Physical_offset_divides_by_scale(double scale, int dx, int dy, double dipX, double dipY)
    {
        var monitor = Monitor(-1920, -200, 2560, 1440, scale);

        var dip = CoordinateMapper.ToMonitorDip(new PixelPoint(-1920 + dx, -200 + dy), monitor);

        Assert.Equal(dipX, dip.X, 1e-9);
        Assert.Equal(dipY, dip.Y, 1e-9);
    }

    [Fact]
    public void Monitor_origin_is_dip_zero()
    {
        var monitor = Monitor(-2560, -360, 2560, 1440, 1.75);

        Assert.Equal(new DipPoint(0, 0), CoordinateMapper.ToMonitorDip(new PixelPoint(-2560, -360), monitor));
    }

    [Fact]
    public void Fractional_overload_matches_integer_overload()
    {
        var monitor = Monitor(1920, 0, 2560, 1440, 1.25);

        Assert.Equal(
            CoordinateMapper.ToMonitorDip(new PixelPoint(2001, 77), monitor),
            CoordinateMapper.ToMonitorDip(2001.0, 77.0, monitor));
    }

    [Theory]
    [InlineData(1.25, 10.3, 10.4)]
    [InlineData(1.5, 3.1, 3.3333333333333335)]
    [InlineData(1.75, -2.0, -2.2857142857142856)]
    public void Snap_lands_on_a_whole_physical_pixel(double scale, double dip, double expected)
    {
        var snapped = CoordinateMapper.SnapToPixel(new DipPoint(dip, dip), scale);

        Assert.Equal(expected, snapped.X, 1e-9);
        Assert.Equal(Math.Round(snapped.X * scale), snapped.X * scale, 1e-9);
    }

    [Fact]
    public void Right_and_bottom_edges_belong_to_the_next_monitor()
    {
        var left = Monitor(0, 0, 1920, 1080, 1.0);
        var right = Monitor(1920, 0, 2560, 1440, 1.5);
        MonitorInfo[] monitors = [left, right];

        Assert.Same(left, CoordinateMapper.MonitorAt(monitors, new PixelPoint(1919, 500)));
        Assert.Same(right, CoordinateMapper.MonitorAt(monitors, new PixelPoint(1920, 500)));
        Assert.Null(CoordinateMapper.MonitorAt(monitors, new PixelPoint(100, 1080)));
    }

    [Fact]
    public void Point_in_a_gap_resolves_to_the_nearest_monitor()
    {
        // Right monitor is lower than the left one, leaving dead space top-right.
        var left = Monitor(0, 0, 1920, 1080, 1.0);
        var right = Monitor(1920, 600, 2560, 1440, 1.5);
        MonitorInfo[] monitors = [left, right];

        Assert.Null(CoordinateMapper.MonitorAt(monitors, new PixelPoint(2000, 100)));
        Assert.Same(left, CoordinateMapper.NearestMonitor(monitors, new PixelPoint(2000, 100)));
        Assert.Same(right, CoordinateMapper.NearestMonitor(monitors, new PixelPoint(2500, 500)));
        Assert.Null(CoordinateMapper.NearestMonitor([], new PixelPoint(0, 0)));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Monitor_rejects_a_non_positive_scale(double scale) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Monitor(0, 0, 100, 100, scale));

    private static MonitorInfo Monitor(int left, int top, int width, int height, double scale)
    {
        var bounds = new PixelRect(left, top, left + width, top + height);
        return new MonitorInfo($"\\\\.\\DISPLAY{left}_{top}", bounds, bounds with { Bottom = bounds.Bottom - 48 }, scale, left == 0 && top == 0);
    }

    // Corners, edges, centre and a deterministic scatter: enough to hit every rounding phase of the scale.
    private static IEnumerable<PixelPoint> Samples(PixelRect bounds)
    {
        yield return new PixelPoint(bounds.Left, bounds.Top);
        yield return new PixelPoint(bounds.Right - 1, bounds.Top);
        yield return new PixelPoint(bounds.Left, bounds.Bottom - 1);
        yield return new PixelPoint(bounds.Right - 1, bounds.Bottom - 1);
        yield return new PixelPoint(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));

        for (var i = 0; i < 64; i++)
        {
            yield return new PixelPoint(bounds.Left + i, bounds.Top + (i * 3));
        }

        var random = new Random(1234);
        for (var i = 0; i < 2000; i++)
        {
            yield return new PixelPoint(random.Next(bounds.Left, bounds.Right), random.Next(bounds.Top, bounds.Bottom));
        }
    }
}
