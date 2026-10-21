using Clicky.Core.Geometry;

namespace Clicky.Core.Ink;

/// <summary>
/// Reads a pointer trail as gestures on its screenshot: loops around things, sweeps under them,
/// rests on them, straight moves and other paths. Pure, so it is unit-tested without a screen.
/// </summary>
/// <remarks>
/// Thresholds are in screenshot pixels because they describe what the model sees. The trail is
/// mapped with the capture's geometry; parts off the screenshot (another monitor, outside the
/// captured window) are dropped and split the trail.
/// </remarks>
public static class InkAnalyzer
{
    /// <summary>Most points listed for a <see cref="InkGestureKind.Path"/>.</summary>
    public const int MaxPathPoints = 8;

    /// <summary>Most gestures described; the least telling (moves, then paths) are dropped first.</summary>
    public const int MaxGestures = 12;

    /// <summary>A pointer that stays within this many pixels of where it stopped counts as resting.</summary>
    internal const double PauseRadius = 4;

    /// <summary>Movement shorter than this between rests is not described (it is still drawn).</summary>
    internal const double MinPartLength = 20;

    /// <summary>A trail shorter than this in all is not worth sending.</summary>
    internal const double MinTrailLength = 24;

    internal const double MinLoopSide = 8;

    /// <summary>Loop area over its box's area: a circle is 0.79, a thin back-and-forth sliver near 0.</summary>
    internal const double MinLoopFill = 0.3;

    /// <summary>Loops whose boxes overlap this much (of the smaller one) are one circle drawn several times.</summary>
    internal const double LoopMergeOverlap = 0.5;

    /// <summary>A short open stretch between two loops of the same circle is part of the circling.</summary>
    internal const double LoopBridgeLength = 40;

    /// <summary>How far back a crossing is looked for, in simplified segments; keeps a long scribble from going quadratic.</summary>
    internal const int MaxLoopSegments = 400;

    internal const double SimplifyForAnalysis = 1.0;
    internal const double SimplifyForDrawing = 0.75;

    /// <summary>A straight sweep faster than this is a flick to somewhere, not an underline.</summary>
    internal static readonly TimeSpan FlickMax = TimeSpan.FromMilliseconds(250);

    internal static readonly TimeSpan PauseMin = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The trail as spatial context for a screenshot with <paramref name="geometry"/>, or null if
    /// the pointer barely moved over the screenshot.
    /// </summary>
    public static SpatialInk? Analyze(InkTrail trail, ScreenshotGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(trail);
        ArgumentNullException.ThrowIfNull(geometry);

        var runs = MapOntoScreenshot(trail, geometry);
        var gestures = new List<InkGesture>();
        var strokes = new List<IReadOnlyList<ScreenshotPoint>>();
        var totalLength = 0.0;
        foreach (var run in runs)
        {
            var points = run.Select(p => p.P).ToList();
            totalLength += Polyline.Length(points);
            var drawn = Polyline.Simplify(points, SimplifyForDrawing).Select(i => points[i]).ToList();
            if (drawn.Count >= 2)
            {
                strokes.Add(drawn);
            }

            ReadRun(run, gestures);
        }

        if (totalLength < MinTrailLength || gestures.Count == 0)
        {
            return null;
        }

        gestures.Sort((a, b) => a.Start.CompareTo(b.Start));
        Trim(gestures);
        return new SpatialInk(gestures, strokes);
    }

    /// <summary>Consecutive on-screenshot samples, each knowing until when the pointer stayed there.</summary>
    private static List<List<TimedPoint>> MapOntoScreenshot(InkTrail trail, ScreenshotGeometry geometry)
    {
        var runs = new List<List<TimedPoint>>();
        List<TimedPoint>? run = null;
        var samples = trail.Samples;
        for (var i = 0; i < samples.Count; i++)
        {
            var p = CoordinateMapper.ToScreenshot(samples[i].Position, geometry);
            var inside = p.X >= -0.5 && p.Y >= -0.5 && p.X <= geometry.ImageWidth - 0.5 && p.Y <= geometry.ImageHeight - 0.5;
            if (!inside)
            {
                run = null;
                continue;
            }

            if (run is null)
            {
                run = [];
                runs.Add(run);
            }

            var until = i + 1 < samples.Count ? samples[i + 1].Time : trail.Duration;
            run.Add(new TimedPoint(p, samples[i].Time, until, IsFirst: i == 0));
        }

        return runs;
    }

    /// <summary>Splits a run at its rests and reads the movement in between.</summary>
    private static void ReadRun(List<TimedPoint> run, List<InkGesture> gestures)
    {
        var pieceStart = 0;
        var i = 0;
        while (i < run.Count)
        {
            var anchor = run[i].P;
            var j = i;
            while (j + 1 < run.Count && Polyline.Distance(run[j + 1].P, anchor) <= PauseRadius)
            {
                j++;
            }

            var rest = run[j].Until - run[i].T;
            if (rest < PauseMin)
            {
                i++;
                continue;
            }

            ReadMovement(run, pieceStart, i, gestures);

            // Resting where the recording began is just where the pointer was; the screenshot line says that.
            if (!run[i].IsFirst)
            {
                var spot = Centroid(run, i, j);
                gestures.Add(new InkGesture(InkGestureKind.Pause, new InkRect(spot.X, spot.Y, spot.X, spot.Y), [spot], run[i].T, rest));
            }

            pieceStart = j;
            i = j + 1;
        }

        ReadMovement(run, pieceStart, run.Count - 1, gestures);
    }

    /// <summary>Reads run[first..last] (inclusive), the movement between two rests.</summary>
    private static void ReadMovement(List<TimedPoint> run, int first, int last, List<InkGesture> gestures)
    {
        if (last <= first)
        {
            return;
        }

        var points = new List<ScreenshotPoint>(last - first + 1);
        for (var k = first; k <= last; k++)
        {
            points.Add(run[k].P);
        }

        var kept = Polyline.Simplify(points, SimplifyForAnalysis);
        var simplified = kept.Select(k => run[first + k]).ToList();
        foreach (var part in MergeLoops(SplitLoops(simplified)))
        {
            if (Classify(part) is { } gesture)
            {
                gestures.Add(gesture);
            }
        }
    }

    /// <summary>
    /// Walks the movement and cuts out each loop where the path crosses itself: the open stretch
    /// before it, the loop, then carries on from the crossing. The smallest loop closing at each
    /// segment wins, so circling twice gives two loops (merged later).
    /// </summary>
    private static List<Part> SplitLoops(List<TimedPoint> s)
    {
        var parts = new List<Part>();
        var current = new List<ScreenshotPoint> { s[0].P };
        var currentStart = s[0].T;
        var segmentFrom = 0;

        // 1 once current starts with a crossing point rather than s[segmentFrom].
        var offset = 0;
        for (var k = 1; k < s.Count; k++)
        {
            var closed = false;
            for (var m = k - 3; m >= Math.Max(segmentFrom, k - MaxLoopSegments); m--)
            {
                if (!Polyline.TryIntersect(s[m].P, s[m + 1].P, s[k - 1].P, s[k].P, out var crossing))
                {
                    continue;
                }

                var loop = new List<ScreenshotPoint>(k - m) { crossing };
                for (var q = m + 1; q <= k - 1; q++)
                {
                    loop.Add(s[q].P);
                }

                if (!IsLoop(loop))
                {
                    continue;
                }

                // current holds s[segmentFrom..k-1] (after a crossing); the open stretch ends at s[m] then the crossing.
                var open = current.Take(m - segmentFrom + 1 + offset).ToList();
                open.Add(crossing);
                parts.Add(new Part(false, open, currentStart, s[m].T));
                parts.Add(new Part(true, loop, s[m].T, s[k].T));
                current = [crossing, s[k].P];
                currentStart = s[k - 1].T;
                segmentFrom = k;
                offset = 1;
                closed = true;
                break;
            }

            if (!closed)
            {
                current.Add(s[k].P);
            }
        }

        if (current.Count >= 2)
        {
            parts.Add(new Part(false, current, currentStart, s[^1].T));
        }

        return parts;
    }

    private static bool IsLoop(List<ScreenshotPoint> loop)
    {
        var box = InkRect.Of(loop);
        if (box.Width < MinLoopSide || box.Height < MinLoopSide)
        {
            return false;
        }

        return Polyline.Area(loop) / (box.Width * box.Height) >= MinLoopFill;
    }

    /// <summary>Joins loops drawn over the same spot (and short stretches between them) into one.</summary>
    private static List<Part> MergeLoops(List<Part> parts)
    {
        var merged = new List<Part>(parts.Count);
        foreach (var part in parts)
        {
            if (part.IsLoop && merged.Count > 0)
            {
                var previous = merged[^1];
                var bridged = false;
                if (!previous.IsLoop && merged.Count > 1 && merged[^2].IsLoop && Polyline.Length(previous.Points) < LoopBridgeLength)
                {
                    previous = merged[^2];
                    bridged = true;
                }

                if (previous.IsLoop && InkRect.Of(previous.Points).OverlapOfSmaller(InkRect.Of(part.Points)) >= LoopMergeOverlap)
                {
                    merged.RemoveAt(merged.Count - 1);
                    if (bridged)
                    {
                        merged.RemoveAt(merged.Count - 1);
                    }

                    merged.Add(new Part(true, [.. previous.Points, .. part.Points], previous.Start, part.End));
                    continue;
                }
            }

            merged.Add(part);
        }

        // The overshoot as a circle closes, or the run-up into it, is part of the circling.
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var part = merged[i];
            if (!part.IsLoop && Polyline.Length(part.Points) < LoopBridgeLength
                && ((i > 0 && Hugs(merged[i - 1], part)) || (i + 1 < merged.Count && Hugs(merged[i + 1], part))))
            {
                merged.RemoveAt(i);
            }
        }

        return merged;

        static bool Hugs(Part loop, Part open)
        {
            if (!loop.IsLoop)
            {
                return false;
            }

            var box = InkRect.Of(loop.Points);
            var margin = 0.25 * Math.Max(box.Width, box.Height);
            var around = new InkRect(box.Left - margin, box.Top - margin, box.Right + margin, box.Bottom + margin);
            return around.Contains(open.Points[0]) && around.Contains(open.Points[^1]);
        }
    }

    private static InkGesture? Classify(Part part)
    {
        var points = part.Points;
        var box = InkRect.Of(points);
        var duration = part.End - part.Start;
        if (part.IsLoop)
        {
            return new InkGesture(InkGestureKind.Circle, box, [], part.Start, duration);
        }

        var length = Polyline.Length(points);
        if (length < MinPartLength)
        {
            return null;
        }

        var chord = Polyline.Distance(points[0], points[^1]);
        var straightness = chord / length;

        // A loop the user did not quite close.
        if (length >= 40 && chord <= 0.3 * Math.Max(box.Width, box.Height) && Math.Min(box.Width, box.Height) >= 10
            && Math.Abs(Polyline.Turning(points)) >= 5.0)
        {
            return new InkGesture(InkGestureKind.Circle, box, [], part.Start, duration);
        }

        // Straight and unhurried, or back and forth along a line (sideways travel well over its width).
        if (box.Width >= 24 && box.Height <= 0.3 * box.Width
            && (straightness >= 0.8 ? duration > FlickMax : HorizontalTravel(points) >= 1.6 * box.Width))
        {
            var y = points.Average(p => p.Y);
            return new InkGesture(InkGestureKind.Underline, box, [new(box.Left, y), new(box.Right, y)], part.Start, duration);
        }

        if (straightness >= 0.85)
        {
            return new InkGesture(InkGestureKind.Move, box, [points[0], points[^1]], part.Start, duration);
        }

        return new InkGesture(InkGestureKind.Path, box, Polyline.SimplifyTo(points, MaxPathPoints, 2.0), part.Start, duration);
    }

    /// <summary>Drops the least telling gestures (moves, then paths, smallest first) beyond <see cref="MaxGestures"/>.</summary>
    private static void Trim(List<InkGesture> gestures)
    {
        while (gestures.Count > MaxGestures)
        {
            var drop = gestures
                .Select((g, index) => (g, index))
                .OrderBy(x => x.g.Kind switch { InkGestureKind.Move => 0, InkGestureKind.Path => 1, _ => 2 })
                .ThenBy(x => x.g.Bounds.Width + x.g.Bounds.Height)
                .First().index;
            gestures.RemoveAt(drop);
        }
    }

    private static double HorizontalTravel(List<ScreenshotPoint> points)
    {
        var travel = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            travel += Math.Abs(points[i].X - points[i - 1].X);
        }

        return travel;
    }

    private static ScreenshotPoint Centroid(List<TimedPoint> run, int first, int last)
    {
        double x = 0, y = 0;
        for (var k = first; k <= last; k++)
        {
            x += run[k].P.X;
            y += run[k].P.Y;
        }

        var n = last - first + 1;
        return new ScreenshotPoint(x / n, y / n);
    }

    /// <param name="Until">When the pointer left this spot (the next sample, or the end of the recording).</param>
    private readonly record struct TimedPoint(ScreenshotPoint P, TimeSpan T, TimeSpan Until, bool IsFirst);

    private sealed record Part(bool IsLoop, List<ScreenshotPoint> Points, TimeSpan Start, TimeSpan End);
}
