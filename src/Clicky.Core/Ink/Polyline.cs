using Clicky.Core.Geometry;

namespace Clicky.Core.Ink;

/// <summary>Plane geometry on polylines of screenshot points.</summary>
internal static class Polyline
{
    public static double Distance(ScreenshotPoint a, ScreenshotPoint b) => Math.Sqrt(DistanceSquared(a, b));

    public static double DistanceSquared(ScreenshotPoint a, ScreenshotPoint b) =>
        ((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y));

    public static double Length(IReadOnlyList<ScreenshotPoint> points)
    {
        var length = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            length += Distance(points[i - 1], points[i]);
        }

        return length;
    }

    /// <summary>Distance from <paramref name="p"/> to the segment a-b.</summary>
    public static double DistanceToSegment(ScreenshotPoint p, ScreenshotPoint a, ScreenshotPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 0)
        {
            return Distance(p, a);
        }

        var t = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        return Distance(p, new ScreenshotPoint(a.X + (t * dx), a.Y + (t * dy)));
    }

    /// <summary>
    /// Indices of the points Ramer-Douglas-Peucker keeps at tolerance <paramref name="epsilon"/>:
    /// always the first and last, plus every point further than that from the simplified line.
    /// </summary>
    public static List<int> Simplify(IReadOnlyList<ScreenshotPoint> points, double epsilon)
    {
        var keep = new List<int>();
        if (points.Count == 0)
        {
            return keep;
        }

        if (points.Count <= 2)
        {
            keep.AddRange(Enumerable.Range(0, points.Count));
            return keep;
        }

        var marked = new bool[points.Count];
        marked[0] = marked[^1] = true;

        // Explicit stack: a long trail would overflow recursion.
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            var worst = -1;
            var worstDistance = epsilon;
            for (var i = first + 1; i < last; i++)
            {
                var d = DistanceToSegment(points[i], points[first], points[last]);
                if (d > worstDistance)
                {
                    worst = i;
                    worstDistance = d;
                }
            }

            if (worst < 0)
            {
                continue;
            }

            marked[worst] = true;
            stack.Push((first, worst));
            stack.Push((worst, last));
        }

        for (var i = 0; i < marked.Length; i++)
        {
            if (marked[i])
            {
                keep.Add(i);
            }
        }

        return keep;
    }

    /// <summary>Simplifies with a growing tolerance until at most <paramref name="maxPoints"/> remain.</summary>
    public static List<ScreenshotPoint> SimplifyTo(IReadOnlyList<ScreenshotPoint> points, int maxPoints, double startEpsilon)
    {
        var epsilon = startEpsilon;
        while (true)
        {
            var kept = Simplify(points, epsilon);
            if (kept.Count <= maxPoints)
            {
                return [.. kept.Select(i => points[i])];
            }

            epsilon *= 1.5;
        }
    }

    /// <summary>
    /// Where segments a1-a2 and b1-b2 cross, strictly inside both (touching at an end does not count).
    /// </summary>
    public static bool TryIntersect(ScreenshotPoint a1, ScreenshotPoint a2, ScreenshotPoint b1, ScreenshotPoint b2, out ScreenshotPoint crossing)
    {
        crossing = default;
        var rx = a2.X - a1.X;
        var ry = a2.Y - a1.Y;
        var sx = b2.X - b1.X;
        var sy = b2.Y - b1.Y;
        var denominator = (rx * sy) - (ry * sx);
        if (Math.Abs(denominator) < 1e-12)
        {
            // Parallel or collinear: overlapping collinear runs are not a loop.
            return false;
        }

        var qx = b1.X - a1.X;
        var qy = b1.Y - a1.Y;
        var t = ((qx * sy) - (qy * sx)) / denominator;
        var u = ((qx * ry) - (qy * rx)) / denominator;
        if (t <= 0 || t >= 1 || u <= 0 || u >= 1)
        {
            return false;
        }

        crossing = new ScreenshotPoint(a1.X + (t * rx), a1.Y + (t * ry));
        return true;
    }

    /// <summary>Absolute area enclosed by the polygon (the closing edge is implied).</summary>
    public static double Area(IReadOnlyList<ScreenshotPoint> polygon)
    {
        var sum = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return Math.Abs(sum) / 2;
    }

    /// <summary>
    /// Sum of the signed turns between consecutive segments, in radians: about ±2π for one full loop,
    /// near 0 for a line or a zigzag. Zero-length segments are skipped.
    /// </summary>
    public static double Turning(IReadOnlyList<ScreenshotPoint> points)
    {
        var total = 0.0;
        double? previous = null;
        for (var i = 1; i < points.Count; i++)
        {
            var dx = points[i].X - points[i - 1].X;
            var dy = points[i].Y - points[i - 1].Y;
            if ((dx * dx) + (dy * dy) < 1e-9)
            {
                continue;
            }

            var heading = Math.Atan2(dy, dx);
            if (previous is { } last)
            {
                var turn = heading - last;
                while (turn > Math.PI)
                {
                    turn -= 2 * Math.PI;
                }

                while (turn < -Math.PI)
                {
                    turn += 2 * Math.PI;
                }

                total += turn;
            }

            previous = heading;
        }

        return total;
    }
}
