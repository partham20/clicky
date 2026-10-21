using Clicky.Core.Geometry;
using Clicky.Core.Settings;

namespace Clicky.Tests.Geometry;

public sealed class ScreenshotMappingTests
{
    // Physical sizes typical for each scale, at negative and odd offsets, plus odd sizes.
    public static TheoryData<string, int, int, int, int> Monitors() => new()
    {
        { "1080p at 100%", 0, 0, 1920, 1080 },
        { "1440p at 125%, left of primary", -2560, -200, 2560, 1440 },
        { "4K at 150%, above primary", 0, -2160, 3840, 2160 },
        { "4K at 175%, left and above", -3840, -1171, 3840, 2160 },
        { "odd laptop panel at 125%", 1920, 313, 1366, 767 },
        { "odd portrait at 175%", -1201, -1999, 1201, 1999 },
        { "ultrawide at 100%", 3840, -77, 5120, 1440 },
    };

    [Theory]
    [MemberData(nameof(Monitors))]
    public void Every_physical_pixel_round_trips_exactly_through_the_default_downscale(string name, int left, int top, int width, int height)
    {
        var (imageWidth, imageHeight) = CoordinateMapper.DownscaleSize(width, height, CaptureSettings.DefaultMaxLongEdge, CaptureSettings.DefaultMaxPixels);
        var geometry = new ScreenshotGeometry(new PixelRect(left, top, left + width, top + height), imageWidth, imageHeight);

        // The mapping is separable (x only depends on x), so walking every column of one row and
        // every row of one column covers every pixel; one full 2-D pass guards against a mix-up.
        for (var x = geometry.Source.Left; x < geometry.Source.Right; x++)
        {
            AssertRoundTrip(new PixelPoint(x, top + (height / 2)), geometry, name);
        }

        for (var y = geometry.Source.Top; y < geometry.Source.Bottom; y++)
        {
            AssertRoundTrip(new PixelPoint(left + (width / 2), y), geometry, name);
        }

        for (var y = geometry.Source.Top; y < geometry.Source.Bottom; y += 7)
        {
            for (var x = geometry.Source.Left; x < geometry.Source.Right; x++)
            {
                AssertRoundTrip(new PixelPoint(x, y), geometry, name);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Monitors))]
    public void Round_trip_holds_for_every_pixel_at_awkward_scales(string name, int left, int top, int width, int height)
    {
        // Scales that are not nice fractions, including a 1-pixel-wide image.
        foreach (var (imageWidth, imageHeight) in new[] { (width - 1, height - 1), (width / 3, height / 7), (997, 331), (1, 1), (width, height) })
        {
            var geometry = new ScreenshotGeometry(new PixelRect(left, top, left + width, top + height), Math.Max(imageWidth, 1), Math.Max(imageHeight, 1));
            for (var x = geometry.Source.Left; x < geometry.Source.Right; x++)
            {
                var y = top + ((x - left) % height);
                AssertRoundTrip(new PixelPoint(x, y), geometry, name);
            }
        }
    }

    [Fact]
    public void Integer_screenshot_pixel_maps_to_the_centre_of_the_region_it_covers()
    {
        // 3:1 downscale: screenshot pixel 0 covers physical 0..2, pixel 1 covers 3..5.
        var geometry = new ScreenshotGeometry(new PixelRect(-300, 100, 0, 400), 100, 100);

        Assert.Equal(new PixelPoint(-299, 101), CoordinateMapper.ToPhysical(new ScreenshotPoint(0, 0), geometry));
        Assert.Equal(new PixelPoint(-296, 104), CoordinateMapper.ToPhysical(new ScreenshotPoint(1, 1), geometry));
        Assert.Equal(new PixelPoint(-2, 398), CoordinateMapper.ToPhysical(new ScreenshotPoint(99, 99), geometry));

        var centre = CoordinateMapper.ToScreenshot(new PixelPoint(-299, 101), geometry);
        Assert.Equal(0, centre.X, 1e-9);
        Assert.Equal(0, centre.Y, 1e-9);
    }

    [Fact]
    public void Identity_scale_maps_pixels_to_themselves()
    {
        var geometry = new ScreenshotGeometry(new PixelRect(-1920, -40, 0, 1040), 1920, 1080);

        Assert.Equal(new ScreenshotPoint(0, 0), CoordinateMapper.ToScreenshot(new PixelPoint(-1920, -40), geometry));
        Assert.Equal(new ScreenshotPoint(1919, 1079), CoordinateMapper.ToScreenshot(new PixelPoint(-1, 1039), geometry));
        Assert.Equal(new PixelPoint(-1820, 60), CoordinateMapper.ToPhysical(new ScreenshotPoint(100, 100), geometry));
    }

    [Theory]
    [InlineData(-50, -50, 0, 0)]
    [InlineData(5000, 5000, 1365, 766)]
    [InlineData(double.NaN, double.PositiveInfinity, 0, 766)]
    public void Out_of_range_screenshot_points_clamp_to_the_source(double x, double y, int dx, int dy)
    {
        var geometry = new ScreenshotGeometry(new PixelRect(-1366, 0, 0, 767), 1092, 613);

        Assert.Equal(new PixelPoint(-1366 + dx, dy), CoordinateMapper.ToPhysical(new ScreenshotPoint(x, y), geometry));
    }

    [Fact]
    public void Downscale_of_16_by_9_4k_fits_both_caps()
    {
        var (width, height) = CoordinateMapper.DownscaleSize(3840, 2160, 1568, 1_150_000);

        Assert.True(Math.Max(width, height) <= 1568);
        Assert.True((long)width * height <= 1_150_000);
        Assert.InRange((double)width / height, 16.0 / 9 - 0.01, 16.0 / 9 + 0.01);

        // The pixel cap is the binding one here, and we use nearly all of it.
        Assert.True((long)width * height > 1_140_000, $"{width}x{height}");
    }

    [Theory]
    [InlineData(1920, 1080, 1429, 804)]
    [InlineData(1080, 1920, 804, 1429)]
    [InlineData(2000, 500, 1568, 392)]
    [InlineData(1280, 720, 1280, 720)]
    [InlineData(800, 600, 800, 600)]
    [InlineData(1568, 600, 1568, 600)]
    public void Downscale_never_upscales_and_applies_whichever_cap_binds(int width, int height, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), CoordinateMapper.DownscaleSize(width, height, 1568, 1_150_000));

    [Fact]
    public void Downscale_fits_every_shape_within_both_caps()
    {
        var random = new Random(42);
        for (var i = 0; i < 20_000; i++)
        {
            var width = random.Next(1, 16_000);
            var height = random.Next(1, 16_000);
            var maxLongEdge = random.Next(1, 4000);
            var maxPixels = random.Next(1, 4_000_000);

            var (w, h) = CoordinateMapper.DownscaleSize(width, height, maxLongEdge, maxPixels);

            Assert.InRange(w, 1, width);
            Assert.InRange(h, 1, height);
            if (w > 1 && h > 1)
            {
                Assert.True(Math.Max(w, h) <= maxLongEdge);
                Assert.True((long)w * h <= maxPixels);
            }
        }
    }

    private static void AssertRoundTrip(PixelPoint pixel, ScreenshotGeometry geometry, string name)
    {
        var screenshot = CoordinateMapper.ToScreenshot(pixel, geometry);
        var back = CoordinateMapper.ToPhysical(screenshot, geometry);
        if (back != pixel)
        {
            Assert.Fail($"{name} {geometry}: {pixel} -> {screenshot} -> {back}");
        }
    }
}
