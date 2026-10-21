using Clicky.Core.Annotations;

namespace Clicky.Tests.Annotations;

public sealed class RoughSketchTests
{
    private static readonly SketchPoint A = new(100, 200);
    private static readonly SketchPoint B = new(500, 260);

    [Fact]
    public void Same_seed_gives_the_same_strokes_and_a_different_seed_does_not()
    {
        var first = Flatten(RoughSketch.Arrow(A, B, 1.5, 42));
        var again = Flatten(RoughSketch.Arrow(A, B, 1.5, 42));
        var other = Flatten(RoughSketch.Arrow(A, B, 1.5, 43));

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void Line_stays_close_to_the_straight_line_and_its_ends(double unit)
    {
        for (var seed = 1; seed <= 50; seed++)
        {
            var stroke = Assert.Single(RoughSketch.Line(A, B, unit, seed));
            Assert.True(stroke.Points[0].DistanceTo(A) <= 2 * 1.2 * unit, $"seed {seed}: start drifted");
            Assert.True(stroke.Points[^1].DistanceTo(B) <= 2 * 1.2 * unit, $"seed {seed}: end drifted");
            foreach (var p in stroke.Points)
            {
                Assert.True(DistanceToLine(p, A, B) <= 6 * unit, $"seed {seed}: bowed {DistanceToLine(p, A, B):0.0} px");
            }
        }
    }

    [Fact]
    public void Arrow_tip_is_exactly_on_target_and_the_head_comes_after_the_shaft()
    {
        for (var seed = 1; seed <= 50; seed++)
        {
            var strokes = RoughSketch.Arrow(A, B, 1.25, seed);
            Assert.Equal(2, strokes.Count);
            Assert.Equal(B, strokes[0].Points[^1]);
            Assert.Equal(3, strokes[1].Points.Count);
            Assert.Equal(B, strokes[1].Points[1]);

            // The wings point back toward the tail.
            foreach (var wing in new[] { strokes[1].Points[0], strokes[1].Points[2] })
            {
                Assert.True(wing.DistanceTo(A) < B.DistanceTo(A), $"seed {seed}: wing points forward");
                Assert.InRange(wing.DistanceTo(B), 9 * 1.25, 26 * 1.25);
            }
        }
    }

    [Theory]
    [InlineData(10, 1.0)]
    [InlineData(40, 1.5)]
    [InlineData(300, 2.0)]
    public void Ellipse_goes_all_the_way_round_with_an_overlap_and_hugs_its_radius(double radius, double unit)
    {
        var centre = new SketchPoint(800, 600);
        for (var seed = 1; seed <= 50; seed++)
        {
            var stroke = Assert.Single(RoughSketch.Ellipse(centre, radius, radius, unit, seed));
            var wobble = Math.Clamp(radius * 0.05, unit, 4 * unit);
            var slack = wobble + (1.6 * wobble / 2) + 0.001;
            foreach (var p in stroke.Points)
            {
                Assert.InRange(p.DistanceTo(centre), radius - slack, radius + slack);
            }

            // Unwrapped angle covers more than a full turn: the ends overlap rather than meet.
            var swept = 0.0;
            for (var i = 1; i < stroke.Points.Count; i++)
            {
                var a0 = Math.Atan2(stroke.Points[i - 1].Y - centre.Y, stroke.Points[i - 1].X - centre.X);
                var a1 = Math.Atan2(stroke.Points[i].Y - centre.Y, stroke.Points[i].X - centre.X);
                var d = a1 - a0;
                swept += d > Math.PI ? d - Math.Tau : d < -Math.PI ? d + Math.Tau : d;
            }

            Assert.InRange(Math.Abs(swept), Math.Tau + 0.3, Math.Tau + 0.75);
        }
    }

    [Fact]
    public void Rectangle_is_two_passes_of_four_sides_that_reach_every_corner()
    {
        var box = new SketchBounds(100, 100, 400, 250);
        var strokes = RoughSketch.Rectangle(box, 1.0, 7);
        Assert.Equal(8, strokes.Count);

        SketchPoint[] corners = [new(100, 100), new(400, 100), new(400, 250), new(100, 250)];
        foreach (var corner in corners)
        {
            var nearest = strokes.Take(4).SelectMany(s => s.Points).Min(p => p.DistanceTo(corner));
            Assert.True(nearest <= 8, $"corner {corner} missed by {nearest:0.0} px");
        }

        // Clockwise from the top-left: the first stroke is the top edge going right.
        Assert.True(strokes[0].Points[^1].X > strokes[0].Points[0].X);
        Assert.InRange(strokes[0].Points[0].Y, 95, 105);
    }

    [Fact]
    public void Underline_return_stroke_sits_below_the_first_whichever_way_it_is_drawn()
    {
        foreach (var (from, to) in new[] { (A, B), (B, A) })
        {
            var strokes = RoughSketch.Underline(from, to, 1.0, 3);
            Assert.Equal(2, strokes.Count);

            // The return stroke starts just under where the first one ended and heads back.
            var back = strokes[1].Points[0];
            Assert.InRange(back.Y - to.Y, 3, 9);
            Assert.True(strokes[1].Points[^1].DistanceTo(from) < back.DistanceTo(from));
        }
    }

    [Fact]
    public void Freehand_passes_near_every_point_it_was_given()
    {
        SketchPoint[] points = [new(10, 10), new(60, 40), new(120, 10), new(180, 50), new(240, 20)];
        var stroke = Assert.Single(RoughSketch.Freehand(points, 1.0, 5));
        foreach (var p in points)
        {
            Assert.True(stroke.Points.Min(q => q.DistanceTo(p)) <= 2, $"missed {p}");
        }
    }

    [Fact]
    public void Freehand_with_one_point_is_a_dot_and_with_none_is_nothing()
    {
        Assert.Single(Assert.Single(RoughSketch.Freehand([new SketchPoint(5, 5)], 1, 1)).Points);
        Assert.Empty(RoughSketch.Freehand([], 1, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Unit_must_be_positive(double unit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RoughSketch.Ellipse(A, 10, 10, unit, 1));

    private static SketchPoint[] Flatten(IReadOnlyList<SketchStroke> strokes) => [.. strokes.SelectMany(s => s.Points)];

    private static double DistanceToLine(SketchPoint p, SketchPoint a, SketchPoint b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        return Math.Abs((dy * (p.X - a.X)) - (dx * (p.Y - a.Y))) / Math.Sqrt((dx * dx) + (dy * dy));
    }
}
