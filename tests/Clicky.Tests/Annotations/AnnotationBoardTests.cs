using Clicky.Core.Actions;
using Clicky.Core.Annotations;

namespace Clicky.Tests.Annotations;

public sealed class AnnotationBoardTests
{
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(300);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void A_new_pointer_replaces_the_old_one_and_flies_from_where_it_was()
    {
        var board = new AnnotationBoard();
        var first = board.Add(Pointer(100, 100), Timeline(Ms(0)), null, Ms(0));
        board.Update(Ms(1000));

        Assert.Equal(new SketchPoint(100, 100), board.PointerPosition(Ms(1000)));

        var second = board.Add(Pointer(500, 300), Timeline(Ms(1000), flight: 300), new SketchPoint(100, 100), Ms(1000));
        Assert.Equal(Ms(1000), first.Timeline.FadeStart);
        Assert.Equal(new SketchPoint(100, 100), second.PointerPosition);

        board.Update(Ms(1150));
        var midway = second.PointerPosition!.Value;
        Assert.InRange(midway.X, 101, 499);

        // While the second one flies, a third starts from where the second one is now.
        Assert.Equal(midway, board.PointerPosition(Ms(1150)));
    }

    [Fact]
    public void Other_marks_pile_up_to_the_limit_then_the_oldest_fades()
    {
        var board = new AnnotationBoard();
        var entries = Enumerable.Range(0, AnnotationBoard.MaxMarks + 2)
            .Select(i => board.Add(Line(i), Timeline(Ms(i)), null, Ms(i)))
            .ToList();

        Assert.True(entries[0].Timeline.DismissedAt.HasValue);
        Assert.True(entries[1].Timeline.DismissedAt.HasValue);
        Assert.All(entries.Skip(2), e => Assert.Null(e.Timeline.DismissedAt));
    }

    [Fact]
    public void A_pointer_does_not_dismiss_other_marks()
    {
        var board = new AnnotationBoard();
        var line = board.Add(Line(0), Timeline(Ms(0)), null, Ms(0));
        board.Add(Pointer(1, 1), Timeline(Ms(10)), null, Ms(10));
        Assert.Null(line.Timeline.DismissedAt);
    }

    [Fact]
    public void Update_says_when_to_wake_while_idle_and_drops_finished_marks()
    {
        var board = new AnnotationBoard();
        board.Add(Line(0), Timeline(Ms(0), hold: 2000), null, Ms(0));
        board.Add(Line(1), Timeline(Ms(0), hold: 5000), null, Ms(0));

        var drawing = board.Update(Ms(100));
        Assert.True(drawing.IsAnimating);
        Assert.Null(drawing.NextChange);

        var idle = board.Update(Ms(1000));
        Assert.False(idle.IsAnimating);
        Assert.Equal(Ms(2200), idle.NextChange);

        var firstFading = board.Update(Ms(2300));
        Assert.True(firstFading.IsAnimating);

        var oneLeft = board.Update(Ms(2600));
        Assert.Single(board.Entries);
        Assert.Equal(Ms(5200), oneLeft.NextChange);

        var empty = board.Update(Ms(6000));
        Assert.True(empty.IsEmpty);
        Assert.Empty(board.Entries);
    }

    [Fact]
    public void Marks_held_until_dismissed_never_schedule_a_wake()
    {
        var board = new AnnotationBoard();
        var entry = board.Add(Line(0), new AnnotationTimeline(Ms(0), TimeSpan.Zero, Ms(200), Timeout.InfiniteTimeSpan, Fade), null, Ms(0));
        var state = board.Update(Ms(1000));
        Assert.False(state.IsAnimating);
        Assert.Null(state.NextChange);
        Assert.False(state.IsEmpty);

        Assert.True(board.Dismiss(entry.Id, Ms(1000)));
        Assert.False(board.Dismiss(entry.Id, Ms(1001)));
        Assert.True(board.Update(Ms(1100)).IsAnimating);
        Assert.True(board.Update(Ms(1400)).IsEmpty);
    }

    [Fact]
    public void DismissAll_fades_everything_and_Clear_drops_it_at_once()
    {
        var board = new AnnotationBoard();
        board.Add(Line(0), Timeline(Ms(0)), null, Ms(0));
        board.Add(Pointer(5, 5), Timeline(Ms(0)), null, Ms(0));
        board.DismissAll(Ms(1000));
        Assert.All(board.Entries, e => Assert.Equal(Ms(1000), e.Timeline.FadeStart));
        Assert.True(board.Update(Ms(1300)).IsEmpty);

        board.Add(Line(0), Timeline(Ms(2000)), null, Ms(2000));
        board.Clear();
        Assert.Empty(board.Entries);
        Assert.False(board.Dismiss(1, Ms(2000)));
    }

    [Fact]
    public void Without_a_pointer_there_is_nothing_to_fly_from()
    {
        var board = new AnnotationBoard();
        board.Add(Line(0), Timeline(Ms(0)), null, Ms(0));
        Assert.Null(board.PointerPosition(Ms(10)));
    }

    private static AnnotationTimeline Timeline(TimeSpan start, double flight = 0, double hold = 3000) =>
        new(start, Ms(flight), Ms(200), Ms(hold), Fade, labelOnLanding: true);

    private static Annotation Pointer(double x, double y) =>
        new(AnnotationKind.Point, [new SketchStroke([new(x, y)])], DrawColor.Accent, 1, Target: new SketchPoint(x, y));

    private static Annotation Line(int i) =>
        new(AnnotationKind.Underline, [new SketchStroke([new(0, i), new(100, i)])], DrawColor.Accent, 1);
}
