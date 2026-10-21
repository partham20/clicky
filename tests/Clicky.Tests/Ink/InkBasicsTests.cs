using System.Text.Json;
using Clicky.Core.Assistant;
using Clicky.Core.Geometry;
using Clicky.Core.Ink;
using Clicky.Core.Prompts;
using Clicky.Core.Settings;

namespace Clicky.Tests.Ink;

public sealed class InkBasicsTests
{
    [Fact]
    public void Builder_skips_repeats_keeps_time_moving_forward_and_extends_duration_to_the_last_sample()
    {
        var builder = new InkTrailBuilder();
        Assert.True(builder.Add(new PixelPoint(1, 1), TimeSpan.FromMilliseconds(10)));
        Assert.False(builder.Add(new PixelPoint(1, 1), TimeSpan.FromMilliseconds(20)));
        Assert.True(builder.Add(new PixelPoint(2, 1), TimeSpan.FromMilliseconds(5)));

        var trail = builder.Build(TimeSpan.Zero);

        Assert.Equal(2, trail.Samples.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(10), trail.Samples[1].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(10), trail.Duration);
    }

    [Fact]
    public void Builder_stops_growing_when_full()
    {
        var builder = new InkTrailBuilder();
        for (var i = 0; i < InkTrailBuilder.MaxSamples + 10; i++)
        {
            builder.Add(new PixelPoint(i, 0), TimeSpan.FromMilliseconds(i));
        }

        Assert.Equal(InkTrailBuilder.MaxSamples, builder.Count);
    }

    [Fact]
    public void Simplify_keeps_the_ends_and_the_corner_of_an_L()
    {
        var points = new List<ScreenshotPoint>();
        for (var i = 0; i <= 10; i++)
        {
            points.Add(new(i * 10, 0));
        }

        for (var i = 1; i <= 10; i++)
        {
            points.Add(new(100, i * 10));
        }

        var kept = Polyline.Simplify(points, 1);

        Assert.Equal([0, 10, 20], kept);
        Assert.Equal(3, Polyline.SimplifyTo(points, 3, 0.1).Count);
    }

    [Fact]
    public void Segments_cross_only_strictly_inside_both()
    {
        Assert.True(Polyline.TryIntersect(new(0, 0), new(10, 10), new(0, 10), new(10, 0), out var crossing));
        Assert.Equal(new ScreenshotPoint(5, 5), crossing);
        Assert.False(Polyline.TryIntersect(new(0, 0), new(10, 0), new(10, 0), new(10, 10), out _));
        Assert.False(Polyline.TryIntersect(new(0, 0), new(10, 0), new(0, 1), new(10, 1), out _));
    }

    [Fact]
    public void Area_and_turning_of_a_square()
    {
        ScreenshotPoint[] square = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        Assert.Equal(100, Polyline.Area(square), 9);
        Assert.Equal(Math.PI, Polyline.Turning(square), 9);
        Assert.Equal(2 * Math.PI, Polyline.Turning([.. square, new(0, 0), new(10, 0)]), 9);
        Assert.Equal(0, Polyline.Turning([new(0, 0), new(5, 0), new(10, 0)]), 9);
    }

    [Fact]
    public void Painter_draws_pink_on_the_line_a_white_edge_beside_it_and_nothing_further_away()
    {
        const int width = 40, height = 20;
        var pixels = Grey(width, height);

        InkPainter.Paint(pixels, width, height, [[new(5, 10), new(35, 10)]]);

        var (b, g, r) = Pixel(pixels, width, 20, 10);
        Assert.InRange(r, 0xC8, 0xFF);
        Assert.InRange(g, 0, 0x50);
        Assert.InRange(b, 0x60, 0x90);

        // Just outside the ink, inside the halo: lighter than the grey it was.
        var edge = Pixel(pixels, width, 20, 12);
        Assert.True(edge.G > 0x60, $"edge green {edge.G}");

        Assert.Equal((0x60, 0x60, 0x60), Pixel(pixels, width, 20, 2));
        Assert.Equal((0x60, 0x60, 0x60), Pixel(pixels, width, 1, 10));
        Assert.All(Enumerable.Range(0, width * height), i => Assert.Equal(0xFF, pixels[(i * 4) + 3]));
    }

    [Fact]
    public void Painter_draws_a_dot_for_one_point_and_clips_ink_off_the_image()
    {
        const int width = 10, height = 10;
        var pixels = Grey(width, height);

        InkPainter.Paint(pixels, width, height, [[new(5, 5)], [new(-50, -50), new(-20, 4)], [new(8, 8), new(400, 400)]]);

        Assert.True(Pixel(pixels, width, 5, 5).R > 0xC0);
        Assert.True(Pixel(pixels, width, 9, 9).R > 0xC0);
        InkPainter.Paint(pixels, width, height, []);
        Assert.Throws<ArgumentException>(() => InkPainter.Paint(new byte[10], width, height, []));
    }

    [Fact]
    public void Live_ink_holds_then_fades_and_everything_fades_together_after_stop()
    {
        var drawn = TimeSpan.FromSeconds(1);
        Assert.Equal(1, InkFade.Opacity(drawn, drawn + InkFade.Hold, null));
        Assert.InRange(InkFade.Opacity(drawn, drawn + InkFade.Hold + (InkFade.Fade / 2), null), 0.45, 0.55);
        Assert.Equal(0, InkFade.Opacity(drawn, drawn + InkFade.Hold + InkFade.Fade, null));

        // Stopped right after drawing: the ink does not age, it holds and then fades as a whole.
        var stop = drawn + TimeSpan.FromMilliseconds(100);
        Assert.Equal(1, InkFade.Opacity(drawn, stop + InkFade.HoldAfterStop, stop));
        Assert.Equal(0, InkFade.Opacity(drawn, stop + InkFade.HoldAfterStop + InkFade.FadeAfterStop, stop));
        Assert.Equal(stop + InkFade.HoldAfterStop + InkFade.FadeAfterStop, InkFade.GoneAt(drawn, stop));
        Assert.Equal(drawn + InkFade.Hold + InkFade.Fade, InkFade.GoneAt(drawn, null));
    }

    [Fact]
    public void User_text_adds_the_ink_line_after_the_screenshot_line()
    {
        var ink = new SpatialInk([new InkGesture(InkGestureKind.Move, default, [new(1, 2), new(30, 40)], TimeSpan.Zero, TimeSpan.Zero)], [], DrawnOnScreenshot: false);
        var shot = new RequestScreenshot([1], new ScreenshotGeometry(new PixelRect(0, 0, 100, 100), 100, 100), Ink: ink);

        Assert.Equal(
            "[Screenshot: 100 x 100 pixels.]\n[While talking, the user moved their pointer to show you something. In order, they moved from (1, 2) to (30, 40).]\nThe user said (speech-to-text): what's this",
            PromptLibrary.UserText(new ClaudeRequest("what's this", RequestSource.Voice, shot)));
    }

    [Fact]
    public void System_prompt_explains_the_pink_trail()
    {
        Assert.Contains("pink line", PromptLibrary.SystemPrompt(ActionDelivery.Tools), StringComparison.Ordinal);
    }

    [Fact]
    public void Ink_settings_default_on_and_survive_a_missing_or_null_section()
    {
        Assert.Equal(new InkSettings(true, true, true), ClickySettings.Default.Ink);

        var missing = JsonSerializer.Deserialize("{}", SettingsJsonContext.Default.ClickySettings)!.Normalize();
        Assert.Equal(new InkSettings(), missing.Ink);

        var nulled = JsonSerializer.Deserialize("""{ "ink": null }""", SettingsJsonContext.Default.ClickySettings)!.Normalize();
        Assert.Equal(new InkSettings(), nulled.Ink);

        var partial = JsonSerializer.Deserialize("""{ "ink": { "drawOnScreenshot": false } }""", SettingsJsonContext.Default.ClickySettings)!.Normalize();
        Assert.Equal(new InkSettings(Enabled: true, ShowTrail: true, DrawOnScreenshot: false), partial.Ink);
    }

    private static byte[] Grey(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = pixels[(i * 4) + 1] = pixels[(i * 4) + 2] = 0x60;
            pixels[(i * 4) + 3] = 0xFF;
        }

        return pixels;
    }

    private static (int B, int G, int R) Pixel(byte[] pixels, int width, int x, int y)
    {
        var offset = ((y * width) + x) * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }
}
