using Clicky.Core.Actions;
using Clicky.Core.Annotations;

namespace Clicky.Tests.Annotations;

public sealed class AnnotationTimelineTests
{
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(300);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void A_pointer_flies_then_draws_then_holds_then_fades()
    {
        var timeline = new AnnotationTimeline(Ms(1000), flight: Ms(400), draw: Ms(300), hold: Ms(2000), Fade, labelOnLanding: true);

        var start = timeline.Evaluate(Ms(1000));
        Assert.Equal(0, start.Flight);
        Assert.Equal(0, start.Draw);
        Assert.Equal(1, start.Opacity);
        Assert.True(start.IsAnimating);

        var midFlight = timeline.Evaluate(Ms(1200));
        Assert.InRange(midFlight.Flight, 0.4, 0.6);
        Assert.Equal(0, midFlight.LabelOpacity);

        var landed = timeline.Evaluate(Ms(1400));
        Assert.Equal(1, landed.Flight);
        Assert.Equal(0, landed.Draw);

        var labelUp = timeline.Evaluate(Ms(1600));
        Assert.Equal(1, labelUp.LabelOpacity);
        Assert.InRange(labelUp.Draw, 0.5, 1);

        var holding = timeline.Evaluate(Ms(2000));
        Assert.Equal(1, holding.Draw);
        Assert.False(holding.IsAnimating);
        Assert.Equal(Ms(3700), timeline.FadeStart);

        var fading = timeline.Evaluate(Ms(3850));
        Assert.True(fading.IsAnimating);
        Assert.InRange(fading.Opacity, 0.1, 0.9);
        Assert.False(fading.IsFinished);

        Assert.True(timeline.Evaluate(Ms(4000)).IsFinished);
    }

    [Fact]
    public void Other_marks_show_their_label_once_the_ink_is_down()
    {
        var timeline = new AnnotationTimeline(TimeSpan.Zero, TimeSpan.Zero, Ms(500), Ms(1000), Fade);
        Assert.Equal(0, timeline.Evaluate(Ms(499)).LabelOpacity);
        Assert.Equal(1, timeline.Evaluate(Ms(650)).LabelOpacity);
        Assert.False(timeline.Evaluate(Ms(651)).IsAnimating);
    }

    [Fact]
    public void An_infinite_hold_waits_for_a_dismiss()
    {
        var timeline = new AnnotationTimeline(TimeSpan.Zero, TimeSpan.Zero, Ms(200), Timeout.InfiniteTimeSpan, Fade);
        Assert.Equal(TimeSpan.MaxValue, timeline.FadeStart);
        Assert.Equal(TimeSpan.MaxValue, timeline.End);
        Assert.False(timeline.Evaluate(TimeSpan.FromHours(5)).IsFinished);

        Assert.True(timeline.Dismiss(TimeSpan.FromHours(5)));
        Assert.True(timeline.Evaluate(TimeSpan.FromHours(5) + Fade).IsFinished);
    }

    [Fact]
    public void Dismissing_starts_the_fade_early_and_only_once()
    {
        var timeline = new AnnotationTimeline(TimeSpan.Zero, TimeSpan.Zero, Ms(200), Ms(5000), Fade);
        Assert.True(timeline.Dismiss(Ms(1000)));
        Assert.Equal(Ms(1000), timeline.FadeStart);
        Assert.False(timeline.Dismiss(Ms(1100)));
        Assert.Equal(Ms(1000), timeline.FadeStart);
        Assert.True(timeline.Evaluate(Ms(1300)).IsFinished);
    }

    [Fact]
    public void Dismissing_after_it_started_fading_changes_nothing()
    {
        var timeline = new AnnotationTimeline(TimeSpan.Zero, TimeSpan.Zero, Ms(200), Ms(1000), Fade);
        Assert.False(timeline.Dismiss(Ms(1250)));
        Assert.Equal(Ms(1200), timeline.FadeStart);
    }

    [Fact]
    public void Negative_hold_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnnotationTimeline(TimeSpan.Zero, TimeSpan.Zero, Ms(200), Ms(-5), Fade));

    [Fact]
    public void Draw_time_follows_ink_length_within_limits()
    {
        var tiny = new Annotation(AnnotationKind.Underline, [new SketchStroke([new(0, 0), new(10, 0)])], DrawColor.Accent, 1);
        var medium = new Annotation(AnnotationKind.Underline, [new SketchStroke([new(0, 0), new(700, 0)])], DrawColor.Accent, 1);
        var huge = new Annotation(AnnotationKind.Underline, [new SketchStroke([new(0, 0), new(9000, 0)])], DrawColor.Accent, 1);
        var mediumHiDpi = medium with { Unit = 2 };

        Assert.Equal(Ms(250), AnnotationTimeline.DrawDuration(tiny));
        Assert.Equal(Ms(500), AnnotationTimeline.DrawDuration(medium));
        Assert.Equal(Ms(900), AnnotationTimeline.DrawDuration(huge));
        Assert.Equal(Ms(250), AnnotationTimeline.DrawDuration(mediumHiDpi));
    }

    [Fact]
    public void Flight_time_grows_with_distance_and_skips_tiny_hops()
    {
        Assert.Equal(TimeSpan.Zero, AnnotationTimeline.FlightDuration(2));
        Assert.Equal(Ms(250), AnnotationTimeline.FlightDuration(20));
        var mid = AnnotationTimeline.FlightDuration(600);
        Assert.InRange(mid.TotalMilliseconds, 400, 450);
        Assert.Equal(Ms(650), AnnotationTimeline.FlightDuration(5000));
    }

    [Fact]
    public void Flight_path_starts_and_ends_on_its_points_and_arcs_upward()
    {
        var from = new SketchPoint(0, 500);
        var to = new SketchPoint(1000, 500);
        Assert.Equal(from, PointerFlight.PositionAt(from, to, 0));
        Assert.Equal(to, PointerFlight.PositionAt(from, to, 1));
        Assert.True(PointerFlight.PositionAt(from, to, 0.5).Y < 500);
        Assert.True(PointerFlight.PositionAt(to, from, 0.5).Y < 500);
        Assert.Equal(to, PointerFlight.PositionAt(to, to, 0.3));
    }
}
