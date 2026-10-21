namespace Clicky.Core.Annotations;

/// <summary>A point of a sketch, in physical screen pixels (fractional, so curves stay smooth).</summary>
public readonly record struct SketchPoint(double X, double Y)
{
    public static SketchPoint Lerp(SketchPoint a, SketchPoint b, double t) => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

    public double DistanceTo(SketchPoint other) => Math.Sqrt(((other.X - X) * (other.X - X)) + ((other.Y - Y) * (other.Y - Y)));
}

/// <summary>An axis-aligned box in physical screen pixels.</summary>
public readonly record struct SketchBounds(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public SketchPoint Centre => new((Left + Right) / 2, (Top + Bottom) / 2);

    public static SketchBounds Of(IEnumerable<SketchPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var p in points)
        {
            left = Math.Min(left, p.X);
            top = Math.Min(top, p.Y);
            right = Math.Max(right, p.X);
            bottom = Math.Max(bottom, p.Y);
        }

        return left > right ? default : new SketchBounds(left, top, right, bottom);
    }

    public SketchBounds Union(SketchBounds other) =>
        new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    public SketchBounds Inflate(double by) => new(Left - by, Top - by, Right + by, Bottom + by);

    public bool Intersects(SketchBounds other) =>
        Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;
}

/// <summary>
/// One pen-down-to-pen-up stroke of a sketch, as a polyline. Strokes of an annotation are drawn in
/// order, so a partial draw-in shows the first strokes whole and the next one cut short.
/// </summary>
public sealed class SketchStroke
{
    private readonly double[] _cumulative;

    public SketchStroke(IReadOnlyList<SketchPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("A stroke needs at least one point.", nameof(points));
        }

        Points = points;
        _cumulative = new double[points.Count];
        for (var i = 1; i < points.Count; i++)
        {
            _cumulative[i] = _cumulative[i - 1] + points[i - 1].DistanceTo(points[i]);
        }
    }

    public IReadOnlyList<SketchPoint> Points { get; }

    public double Length => _cumulative[^1];

    /// <summary>The first <paramref name="length"/> pixels of the stroke; the last point is interpolated.</summary>
    public IReadOnlyList<SketchPoint> Take(double length)
    {
        if (length >= Length)
        {
            return Points;
        }

        if (length <= 0)
        {
            return [Points[0]];
        }

        // First vertex at or past the cut.
        var index = Array.BinarySearch(_cumulative, length);
        if (index >= 0)
        {
            return [.. Points.Take(index + 1)];
        }

        index = ~index;
        var from = _cumulative[index - 1];
        var t = (length - from) / (_cumulative[index] - from);
        var result = new SketchPoint[index + 1];
        for (var i = 0; i < index; i++)
        {
            result[i] = Points[i];
        }

        result[index] = SketchPoint.Lerp(Points[index - 1], Points[index], t);
        return result;
    }

    /// <summary>
    /// The strokes as they look <paramref name="progress"/> (0 to 1) of the way through drawing them
    /// at an even pen speed. Strokes not reached yet are left out.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<SketchPoint>> Reveal(IReadOnlyList<SketchStroke> strokes, double progress)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        if (progress >= 1)
        {
            return [.. strokes.Select(s => s.Points)];
        }

        var remaining = Math.Max(0, progress) * strokes.Sum(s => s.Length);
        var result = new List<IReadOnlyList<SketchPoint>>(strokes.Count);
        foreach (var stroke in strokes)
        {
            if (remaining <= 0)
            {
                break;
            }

            result.Add(stroke.Take(remaining));
            remaining -= stroke.Length;
        }

        return result;
    }
}
