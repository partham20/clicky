namespace Clicky.Core.Annotations;

/// <summary>A mark on the board, with its timing and the frame it showed at the last update.</summary>
public sealed class AnnotationEntry(long id, Annotation annotation, AnnotationTimeline timeline, SketchPoint? flyFrom)
{
    public long Id { get; } = id;

    public Annotation Annotation { get; } = annotation;

    public AnnotationTimeline Timeline { get; } = timeline;

    /// <summary>Where a pointer flies from; null if it appears in place.</summary>
    public SketchPoint? FlyFrom { get; } = flyFrom;

    public AnnotationFrame Frame { get; internal set; }

    /// <summary>Where the pointer's tip is in the current frame, or null for other marks.</summary>
    public SketchPoint? PointerPosition => Annotation.Target is { } target
        ? FlyFrom is { } from ? PointerFlight.PositionAt(from, target, Frame.Flight) : target
        : null;
}

/// <param name="IsAnimating">Something changes every frame: keep rendering.</param>
/// <param name="NextChange">While nothing animates, when the next mark starts to fade; null if none will by itself.</param>
public readonly record struct AnnotationBoardState(bool IsAnimating, TimeSpan? NextChange, bool IsEmpty);

/// <summary>
/// Everything currently drawn on screen, and the rules for how marks share it: one pointer at a time
/// (a new one flies from where the last one was), at most <see cref="MaxMarks"/> marks (the oldest
/// fades when there are more). Not thread-safe; the renderer owns it on the UI thread.
/// </summary>
public sealed class AnnotationBoard
{
    public const int MaxMarks = 12;

    private readonly List<AnnotationEntry> _entries = [];
    private long _nextId;

    public IReadOnlyList<AnnotationEntry> Entries => _entries;

    /// <summary>
    /// Where a new pointer should fly from: the current pointer mark's tip if one is showing,
    /// otherwise null (the renderer then uses the mouse pointer).
    /// </summary>
    public SketchPoint? PointerPosition(TimeSpan now)
    {
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            if (entry.Annotation.Kind == AnnotationKind.Point && now < entry.Timeline.FadeStart)
            {
                entry.Frame = entry.Timeline.Evaluate(now);
                return entry.PointerPosition;
            }
        }

        return null;
    }

    public AnnotationEntry Add(Annotation annotation, AnnotationTimeline timeline, SketchPoint? flyFrom, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(timeline);

        if (annotation.Kind == AnnotationKind.Point)
        {
            foreach (var existing in _entries.Where(e => e.Annotation.Kind == AnnotationKind.Point))
            {
                existing.Timeline.Dismiss(now);
            }
        }

        var entry = new AnnotationEntry(++_nextId, annotation, timeline, flyFrom) { Frame = timeline.Evaluate(now) };
        _entries.Add(entry);

        var live = _entries.Where(e => now < e.Timeline.FadeStart).ToList();
        for (var i = 0; i < live.Count - MaxMarks; i++)
        {
            live[i].Timeline.Dismiss(now);
        }

        return entry;
    }

    /// <summary>Starts fading one mark. Returns false if it is gone or already fading.</summary>
    public bool Dismiss(long id, TimeSpan now) => _entries.Find(e => e.Id == id)?.Timeline.Dismiss(now) ?? false;

    /// <summary>Starts fading every mark.</summary>
    public void DismissAll(TimeSpan now)
    {
        foreach (var entry in _entries)
        {
            entry.Timeline.Dismiss(now);
        }
    }

    /// <summary>Drops every mark at once, without a fade.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>Evaluates every mark at <paramref name="now"/>, drops the faded ones and says when to look again.</summary>
    public AnnotationBoardState Update(TimeSpan now)
    {
        var animating = false;
        TimeSpan? next = null;
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            var frame = entry.Timeline.Evaluate(now);
            if (frame.IsFinished)
            {
                _entries.RemoveAt(i);
                continue;
            }

            entry.Frame = frame;
            animating |= frame.IsAnimating;
            var fadeStart = entry.Timeline.FadeStart;
            if (fadeStart > now && fadeStart != TimeSpan.MaxValue && (next is null || fadeStart < next))
            {
                next = fadeStart;
            }
        }

        return new AnnotationBoardState(animating, animating ? null : next, _entries.Count == 0);
    }
}
