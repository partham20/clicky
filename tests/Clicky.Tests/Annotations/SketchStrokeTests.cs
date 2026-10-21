using Clicky.Core.Annotations;

namespace Clicky.Tests.Annotations;

public sealed class SketchStrokeTests
{
    private static readonly SketchStroke LShape = new([new(0, 0), new(10, 0), new(10, 10)]);

    [Fact]
    public void Length_is_the_sum_of_its_segments() => Assert.Equal(20, LShape.Length, 9);

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(5, 2)]
    [InlineData(10, 2)]
    [InlineData(15, 3)]
    [InlineData(25, 3)]
    public void Take_cuts_at_the_length_and_interpolates_the_last_point(double length, int count)
    {
        var part = LShape.Take(length);
        Assert.Equal(count, part.Count);
        var walked = 0.0;
        for (var i = 1; i < part.Count; i++)
        {
            walked += part[i - 1].DistanceTo(part[i]);
        }

        Assert.Equal(Math.Clamp(length, 0, 20), walked, 9);
    }

    [Fact]
    public void Take_lands_mid_segment()
    {
        var part = LShape.Take(15);
        Assert.Equal(new SketchPoint(10, 5), part[^1]);
    }

    [Fact]
    public void Reveal_draws_strokes_in_order_at_an_even_speed()
    {
        var second = new SketchStroke([new(0, 20), new(20, 20)]);
        SketchStroke[] strokes = [LShape, second];

        Assert.Empty(SketchStroke.Reveal(strokes, 0));

        var quarter = SketchStroke.Reveal(strokes, 0.25);
        Assert.Single(quarter);
        Assert.Equal(new SketchPoint(10, 0), quarter[0][^1]);

        var threeQuarters = SketchStroke.Reveal(strokes, 0.75);
        Assert.Equal(2, threeQuarters.Count);
        Assert.Equal(new SketchPoint(10, 20), threeQuarters[1][^1]);

        var all = SketchStroke.Reveal(strokes, 1);
        Assert.Equal(3, all[0].Count);
        Assert.Equal(2, all[1].Count);
    }

    [Fact]
    public void A_stroke_needs_a_point() => Assert.Throws<ArgumentException>(() => new SketchStroke([]));

    [Fact]
    public void Bounds_cover_all_points_and_detect_overlap()
    {
        var bounds = SketchBounds.Of(LShape.Points);
        Assert.Equal(new SketchBounds(0, 0, 10, 10), bounds);
        Assert.True(bounds.Intersects(new SketchBounds(9, 9, 20, 20)));
        Assert.False(bounds.Intersects(new SketchBounds(10, 0, 20, 10)));
        Assert.True(bounds.Inflate(1).Intersects(new SketchBounds(10.5, 0, 20, 10)));
    }
}
