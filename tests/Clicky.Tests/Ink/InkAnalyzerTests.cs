using Clicky.Core.Geometry;
using Clicky.Core.Ink;

namespace Clicky.Tests.Ink;

public sealed class InkAnalyzerTests
{
    // Unscaled: screenshot pixels equal physical pixels, so expectations read directly.
    private static readonly ScreenshotGeometry OneToOne = new(new PixelRect(0, 0, 1000, 800), 1000, 800);

    [Fact]
    public void A_loop_around_something_is_a_circle_with_its_box()
    {
        var trail = new TrailScript()
            .MoveTo(100, 100)
            .LineTo(350, 300, TimeSpan.FromMilliseconds(200))
            .Circle(400, 300, 50, turns: 1.08, TimeSpan.FromMilliseconds(900))
            .LineTo(700, 300, TimeSpan.FromMilliseconds(200))
            .Build();

        var ink = InkAnalyzer.Analyze(trail, OneToOne);

        Assert.NotNull(ink);
        var circle = Assert.Single(ink.Gestures, g => g.Kind == InkGestureKind.Circle);
        AssertNear(350, circle.Bounds.Left, 4);
        AssertNear(450, circle.Bounds.Right, 4);
        AssertNear(250, circle.Bounds.Top, 4);
        AssertNear(350, circle.Bounds.Bottom, 4);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void A_wobbly_hand_drawn_loop_with_a_run_up_is_still_one_circle(int seed)
    {
        var random = new Random(seed);
        var script = new TrailScript().MoveTo(150, 150).LineTo(460, 300, TimeSpan.FromMilliseconds(250));
        var steps = 110;
        var phase = random.NextDouble() * Math.PI;
        for (var i = 1; i <= steps; i++)
        {
            var angle = 2 * Math.PI * 1.12 * i / steps;
            var radius = 60 * (1 + (0.12 * Math.Sin((3 * angle) + phase))) + random.NextDouble() - 0.5;
            script.LineTo(400 + (radius * Math.Cos(angle)), 300 + (0.8 * radius * Math.Sin(angle)), TimeSpan.FromMilliseconds(8));
        }

        var ink = InkAnalyzer.Analyze(script.LineTo(800, 650, TimeSpan.FromMilliseconds(200)).Build(), OneToOne);

        Assert.NotNull(ink);
        var circle = Assert.Single(ink.Gestures, g => g.Kind == InkGestureKind.Circle);
        AssertNear(400, circle.Bounds.Centre.X, 12);
        AssertNear(300, circle.Bounds.Centre.Y, 12);
        Assert.DoesNotContain(ink.Gestures, g => g.Kind is InkGestureKind.Path or InkGestureKind.Underline);
    }

    [Fact]
    public void Circling_the_same_spot_several_times_is_one_circle()
    {
        var trail = new TrailScript()
            .MoveTo(450, 300)
            .Circle(400, 300, 50, turns: 3.1, TimeSpan.FromSeconds(2))
            .LineTo(700, 600, TimeSpan.FromMilliseconds(200))
            .Build();

        var ink = InkAnalyzer.Analyze(trail, OneToOne);

        Assert.NotNull(ink);
        Assert.Single(ink.Gestures, g => g.Kind == InkGestureKind.Circle);
    }

    [Fact]
    public void Two_circles_in_different_places_stay_two_and_keep_their_order()
    {
        var trail = new TrailScript()
            .MoveTo(250, 200)
            .Circle(200, 200, 50, turns: 1.1, TimeSpan.FromMilliseconds(800))
            .LineTo(650, 500, TimeSpan.FromMilliseconds(250))
            .Circle(600, 500, 50, turns: 1.1, TimeSpan.FromMilliseconds(800))
            .Build();

        var ink = InkAnalyzer.Analyze(trail, OneToOne);

        Assert.NotNull(ink);
        var circles = ink.Gestures.Where(g => g.Kind == InkGestureKind.Circle).ToList();
        Assert.Equal(2, circles.Count);
        AssertNear(200, circles[0].Bounds.Centre.X, 5);
        AssertNear(600, circles[1].Bounds.Centre.X, 5);
    }

    [Fact]
    public void A_loop_not_quite_closed_still_counts_as_a_circle()
    {
        var trail = new TrailScript()
            .MoveTo(450, 300)
            .Circle(400, 300, 60, turns: 0.92, TimeSpan.FromMilliseconds(900))
            .Build();

        var ink = InkAnalyzer.Analyze(trail, OneToOne);

        Assert.NotNull(ink);
        Assert.Equal(InkGestureKind.Circle, Assert.Single(ink.Gestures).Kind);
    }

    [Fact]
    public void A_slow_sweep_under_text_is_an_underline_from_left_to_right()
    {
        var trail = new TrailScript()
            .MoveTo(500, 505)
            .LineTo(200, 500, TimeSpan.FromSeconds(1))
            .Build();

        var ink = InkAnalyzer.Analyze(trail, OneToOne);

        Assert.NotNull(ink);
        var underline = Assert.Single(ink.Gestures);
        Assert.Equal(InkGestureKind.Underline, underline.Kind);
        AssertNear(200, underline.Points[0].X, 1);
        AssertNear(500, underline.Points[1].X, 1);
        AssertNear(502.5, underline.Points[0].Y, 1.5);
    }

    [Fact]
    public void Sweeping_back_and_forth_under_a_line_is_an_underline()
    {
        var trail = new TrailScript()
            .MoveTo(200, 500)
            .LineTo(450, 503, TimeSpan.FromMilliseconds(150))
            .LineTo(210, 506, TimeSpan.FromMilliseconds(150))
            .LineTo(455, 502, TimeSpan.FromMilliseconds(150))
            .Build();

        var ink = InkAnalyzer.Analyze(trail, OneToOne);

        Assert.NotNull(ink);
        Assert.Equal(InkGestureKind.Underline, Assert.Single(ink.Gestures).Kind);
    }

    [Fact]
    public void A_quick_straight_flick_is_a_move()
    {
        var trail = new TrailScript()
            .MoveTo(100, 100)
            .LineTo(600, 120, TimeSpan.FromMilliseconds(150))
            .Build();

        var ink = InkAnalyzer.Analyze(trail, OneToOne);

        Assert.NotNull(ink);
        var move = Assert.Single(ink.Gestures);
        Assert.Equal(InkGestureKind.Move, move.Kind);
        Assert.Equal(new ScreenshotPoint(100, 100), move.Points[0]);
        Assert.Equal(new ScreenshotPoint(600, 120), move.Points[1]);
    }

    [Fact]
    public void Resting_on_something_is_a_pause_with_how_long_but_the_starting_rest_is_not()
    {
        var trail = new TrailScript()
            .MoveTo(100, 100)
            .Rest(TimeSpan.FromSeconds(1))
            .LineTo(300, 300, TimeSpan.FromMilliseconds(150))
            .Rest(TimeSpan.FromMilliseconds(800))
            .LineTo(300, 600, TimeSpan.FromMilliseconds(150))
            .Build();

        var ink = InkAnalyzer.Analyze(trail, OneToOne);

        Assert.NotNull(ink);
        Assert.Equal([InkGestureKind.Move, InkGestureKind.Pause, InkGestureKind.Move], ink.Gestures.Select(g => g.Kind));
        var pause = ink.Gestures[1];
        Assert.Equal(new ScreenshotPoint(300, 300), pause.Points[0]);
        AssertNear(0.8, pause.Duration.TotalSeconds, 0.02);
    }

    [Fact]
    public void Resting_at_the_end_until_release_counts()
    {
        var trail = new TrailScript()
            .MoveTo(100, 100)
            .LineTo(300, 300, TimeSpan.FromMilliseconds(150))
            .Rest(TimeSpan.FromSeconds(2))
            .Build();

        var ink = InkAnalyzer.Analyze(trail, OneToOne);

        Assert.NotNull(ink);
        var pause = ink.Gestures[^1];
        Assert.Equal(InkGestureKind.Pause, pause.Kind);
        AssertNear(2, pause.Duration.TotalSeconds, 0.02);
    }

    [Fact]
    public void Tiny_jitter_sends_nothing()
    {
        var trail = new TrailScript()
            .MoveTo(100, 100)
            .LineTo(110, 104, TimeSpan.FromMilliseconds(300))
            .Rest(TimeSpan.FromSeconds(1))
            .Build();

        Assert.Null(InkAnalyzer.Analyze(trail, OneToOne));
        Assert.Null(InkAnalyzer.Analyze(InkTrail.Empty, OneToOne));
    }

    [Fact]
    public void A_wandering_path_lists_a_few_points_along_it()
    {
        var script = new TrailScript().MoveTo(100, 400);
        for (var i = 1; i <= 12; i++)
        {
            script.LineTo(100 + (i * 50), i % 2 == 0 ? 400 : 250, TimeSpan.FromMilliseconds(120));
        }

        var ink = InkAnalyzer.Analyze(script.Build(), OneToOne);

        Assert.NotNull(ink);
        var path = Assert.Single(ink.Gestures);
        Assert.Equal(InkGestureKind.Path, path.Kind);
        Assert.InRange(path.Points.Count, 2, InkAnalyzer.MaxPathPoints);
        Assert.Equal(new ScreenshotPoint(100, 400), path.Points[0]);
        Assert.Equal(new ScreenshotPoint(700, 400), path.Points[^1]);
    }

    [Fact]
    public void Ink_off_the_screenshot_is_dropped()
    {
        var offImage = new TrailScript()
            .MoveTo(1100, 100)
            .Circle(1300, 300, 60, turns: 1.1, TimeSpan.FromSeconds(1))
            .Build();
        Assert.Null(InkAnalyzer.Analyze(offImage, OneToOne));

        // Leaves the image mid-way: only the part on it is drawn and read.
        var crossing = new TrailScript()
            .MoveTo(1200, 300)
            .LineTo(600, 300, TimeSpan.FromMilliseconds(200))
            .Build();
        var ink = InkAnalyzer.Analyze(crossing, OneToOne);
        Assert.NotNull(ink);
        Assert.All(ink.Strokes.SelectMany(s => s), p => Assert.InRange(p.X, -0.5, 999.5));
    }

    [Fact]
    public void Coordinates_are_mapped_into_a_downscaled_screenshot_on_another_monitor()
    {
        // A 4K monitor to the left of the primary, captured at half size.
        var geometry = new ScreenshotGeometry(new PixelRect(-3840, 0, 0, 2160), 1920, 1080);
        var trail = new TrailScript()
            .MoveTo(-1800, 1000)
            .Circle(-1900, 1000, 100, turns: 1.1, TimeSpan.FromSeconds(1))
            .Build();

        var ink = InkAnalyzer.Analyze(trail, geometry);

        Assert.NotNull(ink);
        var circle = Assert.Single(ink.Gestures);
        AssertNear(970, circle.Bounds.Centre.X, 3);
        AssertNear(500, circle.Bounds.Centre.Y, 3);
        AssertNear(100, circle.Bounds.Width, 4);
    }

    [Fact]
    public void Too_many_gestures_drop_moves_first()
    {
        var script = new TrailScript().MoveTo(50, 50);
        for (var i = 0; i < 10; i++)
        {
            script.LineTo(50 + (i * 80), i % 2 == 0 ? 400 : 100, TimeSpan.FromMilliseconds(100)).Rest(TimeSpan.FromMilliseconds(500));
        }

        var ink = InkAnalyzer.Analyze(script.Build(), OneToOne);

        Assert.NotNull(ink);
        Assert.Equal(InkAnalyzer.MaxGestures, ink.Gestures.Count);
        Assert.Equal(10, ink.Gestures.Count(g => g.Kind == InkGestureKind.Pause));
        Assert.Equal(ink.Gestures.OrderBy(g => g.Start), ink.Gestures);
    }

    [Fact]
    public void Description_lists_gestures_in_order_and_mentions_the_drawing_only_when_it_is_drawn()
    {
        var ink = new SpatialInk(
            [
                new InkGesture(InkGestureKind.Circle, new InkRect(350.2, 249.6, 450, 350), [], TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                new InkGesture(InkGestureKind.Pause, new InkRect(700, 140, 700, 140), [new(700, 140)], TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1.25)),
                new InkGesture(InkGestureKind.Underline, default, [new(300, 512), new(520, 512)], TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1)),
            ],
            []);

        Assert.Equal(
            "[While talking, the user moved their pointer to show you something. In order, they circled the area (350, 250) to (450, 350); "
            + "then rested the pointer on (700, 140) for 1.3 s; then underlined from (300, 512) to (520, 512).]",
            ink.Describe());
        Assert.Contains("drawn on the screenshot as a pink line", (ink with { DrawnOnScreenshot = true }).Describe(), StringComparison.Ordinal);
    }

    private static void AssertNear(double expected, double actual, double tolerance) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);
}

/// <summary>Builds a trail as a 125 Hz pointer sampler would record it.</summary>
internal sealed class TrailScript
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(8);

    private readonly InkTrailBuilder _builder = new();
    private double _x;
    private double _y;
    private TimeSpan _now;

    public TrailScript MoveTo(double x, double y)
    {
        _x = x;
        _y = y;
        Add();
        return this;
    }

    public TrailScript LineTo(double x, double y, TimeSpan duration)
    {
        var (fromX, fromY) = (_x, _y);
        return Walk(duration, t => (fromX + ((x - fromX) * t), fromY + ((y - fromY) * t)));
    }

    /// <summary>Circles the centre from where the pointer is now (it should be on the circle).</summary>
    public TrailScript Circle(double cx, double cy, double radius, double turns, TimeSpan duration)
    {
        var start = Math.Atan2(_y - cy, _x - cx);
        return Walk(duration, t => (cx + (radius * Math.Cos(start + (t * turns * 2 * Math.PI))), cy + (radius * Math.Sin(start + (t * turns * 2 * Math.PI)))));
    }

    public TrailScript Rest(TimeSpan duration)
    {
        _now += duration;
        return this;
    }

    public InkTrail Build() => _builder.Build(_now);

    private TrailScript Walk(TimeSpan duration, Func<double, (double X, double Y)> at)
    {
        var steps = Math.Max(1, (int)Math.Round(duration / Tick));
        for (var i = 1; i <= steps; i++)
        {
            _now += Tick;
            (_x, _y) = at((double)i / steps);
            Add();
        }

        return this;
    }

    private void Add() => _builder.Add(new PixelPoint((int)Math.Round(_x), (int)Math.Round(_y)), _now);
}
