using Clicky.Core.Actions;
using Clicky.Core.Annotations;
using Clicky.Core.Geometry;

namespace Clicky.Tests.Annotations;

public sealed class AnnotationPlannerTests
{
    // A 2560x1440 monitor left of the primary, captured at the default 1568 long edge.
    private static readonly ScreenshotGeometry Left1440 = new(new PixelRect(-2560, 0, 0, 1440), 1568, 882);

    private static double Unit125(SketchPoint _) => 1.25;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(784, 441)]
    [InlineData(1567, 881)]
    [InlineData(100.4, 700.7)]
    public void Point_lands_on_the_physical_pixel_the_mapper_gives(double x, double y)
    {
        var annotation = Plan(new PointAction(x, y));

        var target = annotation.Target!.Value;
        var expected = CoordinateMapper.ToPhysical(new ScreenshotPoint(x, y), Left1440);
        Assert.InRange(target.X, expected.X - 1, expected.X + 1);
        Assert.InRange(target.Y, expected.Y - 1, expected.Y + 1);
        Assert.Equal(AnnotationKind.Point, annotation.Kind);
        Assert.Equal(1.25, annotation.Unit);
    }

    [Fact]
    public void Point_draws_a_ring_round_its_spot_and_puts_the_label_beside_the_glyph()
    {
        var annotation = Plan(new PointAction(400, 400, "  Share   button "));
        var target = annotation.Target!.Value;

        var ring = Assert.Single(annotation.Strokes);
        Assert.All(ring.Points, p => Assert.InRange(p.DistanceTo(target), 12 * 1.25, 24 * 1.25));
        Assert.Equal("Share button", annotation.Label);
        Assert.Equal(LabelSide.Right, annotation.LabelSide);
        Assert.True(annotation.LabelAnchor.X > target.X);
    }

    [Fact]
    public void Point_near_the_right_edge_puts_its_label_on_the_left()
    {
        var annotation = Plan(new PointAction(1550, 400, "Close"));
        Assert.Equal(LabelSide.Left, annotation.LabelSide);
        Assert.True(annotation.LabelAnchor.X < annotation.Target!.Value.X);
    }

    [Fact]
    public void Off_screen_coordinates_are_refused_with_the_reason()
    {
        Assert.False(AnnotationPlanner.TryPlan(new PointAction(3000, 10), Left1440, Unit125, 1, out var annotation, out var error));
        Assert.Null(annotation);
        Assert.Contains("outside", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_few_pixels_past_the_edge_is_pulled_onto_it()
    {
        var annotation = Plan(new PointAction(1570, 885));

        // The centre of the last screenshot pixel, which is within a physical pixel or two of the corner.
        var target = annotation.Target!.Value;
        Assert.InRange(target.X, -2, -1);
        Assert.InRange(target.Y, 1438, 1439);
    }

    [Fact]
    public void Actions_that_draw_nothing_are_refused()
    {
        Assert.False(AnnotationPlanner.Draws(new SayAction("hi")));
        Assert.False(AnnotationPlanner.Draws(new ClickAction(1, 1)));
        Assert.False(AnnotationPlanner.Draws(new StepAction(1, "Open Settings")));
        Assert.True(AnnotationPlanner.Draws(new StepAction(1, "Open Settings", X: 5, Y: 5)));
        Assert.False(AnnotationPlanner.TryPlan(new SayAction("hi"), Left1440, Unit125, 1, out _, out var error));
        Assert.Equal("say draws nothing", error);
    }

    [Fact]
    public void Highlight_box_maps_edges_pads_and_clips_to_the_screen()
    {
        // Screenshot scale is 1568/2560 = 0.6125, so 61.25 screenshot px = 100 physical px.
        var annotation = Plan(new HighlightAction(61.25, 122.5, 122.5, 61.25, "Toolbar"));
        var pad = AnnotationPlanner.HighlightPadding * 1.25;
        var fill = annotation.Fill!.Value;

        Assert.Equal(-2560 + 100 - pad, fill.Left, 6);
        Assert.Equal(200 - pad, fill.Top, 6);
        Assert.Equal(-2560 + 300 + pad, fill.Right, 6);
        Assert.Equal(300 + pad, fill.Bottom, 6);
        Assert.Equal(8, annotation.Strokes.Count);
        Assert.Equal(LabelSide.Above, annotation.LabelSide);

        var corner = Plan(new HighlightAction(0, 0, 50, 50));
        Assert.Equal(new SketchBounds(-2560, 0, -2560 + (50 / Left1440.ScaleX) + pad, (50 / Left1440.ScaleY) + pad), corner.Fill);
        Assert.Equal(LabelSide.Below, corner.LabelSide);
    }

    [Fact]
    public void Highlight_filling_the_screen_keeps_its_label_inside()
    {
        var annotation = Plan(new HighlightAction(0, 0, 1568, 882, "Everything"));
        Assert.Equal(LabelSide.Below, annotation.LabelSide);
        Assert.InRange(annotation.LabelAnchor.Y, 0, 50);
    }

    [Fact]
    public void Arrow_runs_from_first_to_last_point_and_labels_its_tail()
    {
        var annotation = Plan(new DrawAction(DrawShape.Arrow, [new(100, 100), new(400, 120)], Color: DrawColor.Red, Label: "Here"));

        Assert.Equal(AnnotationKind.Arrow, annotation.Kind);
        Assert.Equal(DrawColor.Red, annotation.Color);
        var tip = annotation.Strokes[0].Points[^1];
        var expected = CoordinateMapper.ToPhysical(new ScreenshotPoint(400, 120), Left1440);
        Assert.InRange(tip.X, expected.X - 1, expected.X + 1);

        // Heading right, so the label sits left of the tail.
        Assert.Equal(LabelSide.Left, annotation.LabelSide);
    }

    [Theory]
    [InlineData(DrawShape.Arrow)]
    [InlineData(DrawShape.Underline)]
    [InlineData(DrawShape.Freehand)]
    public void Shapes_that_need_two_points_refuse_one(DrawShape shape)
    {
        Assert.False(AnnotationPlanner.TryPlan(new DrawAction(shape, [new(10, 10)]), Left1440, Unit125, 1, out _, out var error));
        Assert.Contains("two points", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Circle_radius_is_scaled_to_physical_pixels()
    {
        var annotation = Plan(new DrawAction(DrawShape.Circle, [new(500, 400)], Radius: 61.25));
        var stroke = Assert.Single(annotation.Strokes);
        var centre = CoordinateMapper.ToPhysical(new ScreenshotPoint(500, 400), Left1440);
        var mean = stroke.Points.Average(p => p.DistanceTo(new SketchPoint(centre.X, centre.Y)));
        Assert.InRange(mean, 95, 105);
        Assert.Equal(DrawColor.Accent, annotation.Color);
    }

    [Fact]
    public void Circle_without_a_radius_still_draws()
    {
        var annotation = Plan(new DrawAction(DrawShape.Circle, [new(500, 400)]));
        Assert.Equal(AnnotationKind.Circle, annotation.Kind);
    }

    [Fact]
    public void Step_becomes_a_pointer_with_its_instruction_as_the_label()
    {
        var annotation = Plan(new StepAction(2, "Open the File menu", Total: 5, X: 300, Y: 300));
        Assert.Equal(AnnotationKind.Point, annotation.Kind);
        Assert.True(annotation.IsStep);
        Assert.Equal("Step 2 of 5: Open the File menu", annotation.Label);

        var noTotal = Plan(new StepAction(3, "Click Save", X: 300, Y: 300));
        Assert.Equal("Step 3: Click Save", noTotal.Label);
    }

    [Fact]
    public void Long_labels_are_cut_with_an_ellipsis()
    {
        var annotation = Plan(new StepAction(1, new string('x', 200), X: 10, Y: 10));
        Assert.Equal(AnnotationPlanner.MaxLabelLength, annotation.Label!.Length);
        Assert.EndsWith("…", annotation.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_scale_falls_back_to_one()
    {
        Assert.True(AnnotationPlanner.TryPlan(new PointAction(10, 10), Left1440, _ => double.NaN, 1, out var annotation, out _));
        Assert.Equal(1, annotation!.Unit);
    }

    private static Annotation Plan(ClickyAction action)
    {
        Assert.True(AnnotationPlanner.TryPlan(action, Left1440, Unit125, 7, out var annotation, out var error), error);
        return annotation!;
    }
}
