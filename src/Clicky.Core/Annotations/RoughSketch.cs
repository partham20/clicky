namespace Clicky.Core.Annotations;

/// <summary>
/// Turns clean shapes into strokes that look drawn by hand with a marker: lines bow a little and
/// overshoot their corners, circles wobble and close with an overlap, arrows curve. In the spirit
/// of rough.js, kept subtle so the marks still point exactly where they should.
/// </summary>
/// <remarks>
/// All coordinates are physical pixels. <c>unit</c> is physical pixels per DIP where the shape is
/// drawn, so the wobble looks the same size at every display scale. The same seed always gives the
/// same strokes, which keeps a mark steady while it animates and makes the output testable.
/// </remarks>
public static class RoughSketch
{
    /// <summary>A straight-ish line from <paramref name="a"/> to <paramref name="b"/>: one stroke, bowed and with jittered ends.</summary>
    public static IReadOnlyList<SketchStroke> Line(SketchPoint a, SketchPoint b, double unit, int seed)
    {
        var random = new SketchRandom(seed);
        return [BowedLine(a, b, unit, random, bowFraction: 0.025, maxBow: 5, endJitter: 1.2)];
    }

    /// <summary>A slightly curved shaft from <paramref name="from"/> to <paramref name="to"/>, then the head as one stroke.</summary>
    public static IReadOnlyList<SketchStroke> Arrow(SketchPoint from, SketchPoint to, double unit, int seed)
    {
        CheckUnit(unit);
        var random = new SketchRandom(seed);
        var length = from.DistanceTo(to);
        if (length < unit)
        {
            return [new SketchStroke([from, to])];
        }

        // A consistent bend to one side reads as a hand-drawn arrow; a wiggle would not.
        var (dx, dy) = ((to.X - from.X) / length, (to.Y - from.Y) / length);
        var (nx, ny) = (-dy, dx);
        var bend = random.Sign() * Math.Min(length * random.Range(0.04, 0.08), 18 * unit);
        var c1 = Offset(SketchPoint.Lerp(from, to, random.Range(0.25, 0.4)), nx, ny, bend);
        var c2 = Offset(SketchPoint.Lerp(from, to, random.Range(0.6, 0.75)), nx, ny, bend * random.Range(0.5, 0.9));
        var start = Jitter(from, unit * 1.2, random);
        var shaft = Bezier(start, c1, c2, to, Segments(length, unit));

        // The head follows the shaft's direction where it arrives, not the straight from-to line.
        var last = shaft[^2];
        var arrive = last.DistanceTo(to);
        var (ax, ay) = arrive > 0 ? ((to.X - last.X) / arrive, (to.Y - last.Y) / arrive) : (dx, dy);
        var headLength = Math.Clamp(length * 0.2, 11 * unit, 22 * unit);
        var left = Wing(to, ax, ay, headLength * random.Range(0.9, 1.1), random.Range(26, 34));
        var right = Wing(to, ax, ay, headLength * random.Range(0.9, 1.1), -random.Range(26, 34));
        var head = new[] { Jitter(left, unit, random), to, Jitter(right, unit, random) };

        return [new SketchStroke(shaft), new SketchStroke(head)];
    }

    /// <summary>
    /// A loop around <paramref name="centre"/> that starts and ends at different places and overlaps
    /// itself, like a circle drawn in one go. The wobble never pulls it inside the given radii by more
    /// than a few DIPs, so whatever it circles stays inside.
    /// </summary>
    public static IReadOnlyList<SketchStroke> Ellipse(SketchPoint centre, double radiusX, double radiusY, double unit, int seed)
    {
        CheckUnit(unit);
        var random = new SketchRandom(seed);
        radiusX = Math.Max(radiusX, unit);
        radiusY = Math.Max(radiusY, unit);

        var start = random.Range(0, Math.Tau);
        var sweep = Math.Tau + random.Range(0.35, 0.7);
        var wobble = Math.Clamp(Math.Min(radiusX, radiusY) * 0.05, unit, 4 * unit);
        var (p1, p2) = (random.Range(0, Math.Tau), random.Range(0, Math.Tau));

        // Spiral slightly outward so the overlap sits beside the start rather than on top of it.
        var drift = wobble * random.Range(0.8, 1.6);
        var steps = (int)Math.Clamp(Math.PI * (radiusX + radiusY) / (5 * unit), 24, 120);
        var points = new SketchPoint[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            var angle = start + (sweep * t);
            var delta = (wobble * ((0.6 * Math.Sin((2 * angle) + p1)) + (0.4 * Math.Sin((3 * angle) + p2)))) + (drift * (t - 0.5));
            points[i] = new SketchPoint(centre.X + ((radiusX + delta) * Math.Cos(angle)), centre.Y + ((radiusY + delta) * Math.Sin(angle)));
        }

        return [new SketchStroke(points)];
    }

    /// <summary>
    /// A box drawn as four strokes, clockwise from the top-left, each running a little past its
    /// corners; then a lighter second pass, as a marker box usually gets.
    /// </summary>
    public static IReadOnlyList<SketchStroke> Rectangle(SketchBounds box, double unit, int seed)
    {
        CheckUnit(unit);
        var random = new SketchRandom(seed);
        SketchPoint[] corners =
        [
            new(box.Left, box.Top),
            new(box.Right, box.Top),
            new(box.Right, box.Bottom),
            new(box.Left, box.Bottom),
        ];

        var strokes = new List<SketchStroke>(8);
        for (var pass = 0; pass < 2; pass++)
        {
            for (var side = 0; side < 4; side++)
            {
                var a = corners[side];
                var b = corners[(side + 1) % 4];
                var length = a.DistanceTo(b);
                if (length <= 0)
                {
                    continue;
                }

                var (dx, dy) = ((b.X - a.X) / length, (b.Y - a.Y) / length);
                var before = random.Range(0, 3) * unit;
                var after = random.Range(2, 6) * unit;
                var from = new SketchPoint(a.X - (dx * before), a.Y - (dy * before));
                var to = new SketchPoint(b.X + (dx * after), b.Y + (dy * after));
                strokes.Add(BowedLine(from, to, unit, random, bowFraction: 0.02, maxBow: 4, endJitter: pass == 0 ? 1.5 : 3));
            }
        }

        return strokes;
    }

    /// <summary>A quick underline: one stroke left to right, then a shorter return stroke just below it.</summary>
    public static IReadOnlyList<SketchStroke> Underline(SketchPoint a, SketchPoint b, double unit, int seed)
    {
        CheckUnit(unit);
        var random = new SketchRandom(seed);
        var first = BowedLine(a, b, unit, random, bowFraction: 0.02, maxBow: 3, endJitter: 1);
        var length = a.DistanceTo(b);
        if (length < 8 * unit)
        {
            return [first];
        }

        var (dx, dy) = ((b.X - a.X) / length, (b.Y - a.Y) / length);
        var (nx, ny) = (-dy, dx);
        if (ny < 0)
        {
            // "Below" on screen, whichever way the line runs.
            (nx, ny) = (-nx, -ny);
        }

        var gap = random.Range(5, 7) * unit;
        var back = Offset(b, nx, ny, gap);
        var end = Offset(SketchPoint.Lerp(b, a, random.Range(0.6, 0.9)), nx, ny, gap);
        return [first, BowedLine(back, end, unit, random, bowFraction: 0.02, maxBow: 3, endJitter: 1.5)];
    }

    /// <summary>A smooth stroke through <paramref name="points"/> (Catmull-Rom), each nudged a little.</summary>
    public static IReadOnlyList<SketchStroke> Freehand(IReadOnlyList<SketchPoint> points, double unit, int seed)
    {
        ArgumentNullException.ThrowIfNull(points);
        CheckUnit(unit);
        if (points.Count == 0)
        {
            return [];
        }

        var random = new SketchRandom(seed);
        var nudged = points.Select(p => Jitter(p, unit, random)).ToArray();
        if (nudged.Length == 1)
        {
            return [new SketchStroke(nudged)];
        }

        var result = new List<SketchPoint> { nudged[0] };
        for (var i = 0; i < nudged.Length - 1; i++)
        {
            var p0 = nudged[Math.Max(i - 1, 0)];
            var p1 = nudged[i];
            var p2 = nudged[i + 1];
            var p3 = nudged[Math.Min(i + 2, nudged.Length - 1)];
            var segments = Math.Max(2, Segments(p1.DistanceTo(p2), unit) / 2);
            for (var s = 1; s <= segments; s++)
            {
                result.Add(CatmullRom(p0, p1, p2, p3, (double)s / segments));
            }
        }

        return [new SketchStroke(result)];
    }

    private static SketchStroke BowedLine(SketchPoint a, SketchPoint b, double unit, SketchRandom random, double bowFraction, double maxBow, double endJitter)
    {
        CheckUnit(unit);
        var length = a.DistanceTo(b);
        if (length < unit)
        {
            return new SketchStroke([a, b]);
        }

        var (nx, ny) = (-(b.Y - a.Y) / length, (b.X - a.X) / length);
        var bow = Math.Min(length * bowFraction, maxBow * unit);
        var jitter = Math.Min(length * 0.01, endJitter * unit);
        var start = Jitter(a, jitter, random);
        var end = Jitter(b, jitter, random);
        var c1 = Offset(SketchPoint.Lerp(a, b, random.Range(0.2, 0.4)), nx, ny, random.Range(-bow, bow));
        var c2 = Offset(SketchPoint.Lerp(a, b, random.Range(0.6, 0.8)), nx, ny, random.Range(-bow, bow));
        return new SketchStroke(Bezier(start, c1, c2, end, Segments(length, unit)));
    }

    private static SketchPoint[] Bezier(SketchPoint p0, SketchPoint p1, SketchPoint p2, SketchPoint p3, int segments)
    {
        var points = new SketchPoint[segments + 1];
        for (var i = 0; i <= segments; i++)
        {
            var t = (double)i / segments;
            var u = 1 - t;
            var (w0, w1, w2, w3) = (u * u * u, 3 * u * u * t, 3 * u * t * t, t * t * t);
            points[i] = new SketchPoint(
                (w0 * p0.X) + (w1 * p1.X) + (w2 * p2.X) + (w3 * p3.X),
                (w0 * p0.Y) + (w1 * p1.Y) + (w2 * p2.Y) + (w3 * p3.Y));
        }

        return points;
    }

    private static SketchPoint CatmullRom(SketchPoint p0, SketchPoint p1, SketchPoint p2, SketchPoint p3, double t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        double Axis(double a, double b, double c, double d) =>
            0.5 * ((2 * b) + ((-a + c) * t) + (((2 * a) - (5 * b) + (4 * c) - d) * t2) + ((-a + (3 * b) - (3 * c) + d) * t3));
        return new SketchPoint(Axis(p0.X, p1.X, p2.X, p3.X), Axis(p0.Y, p1.Y, p2.Y, p3.Y));
    }

    // About one vertex every 6 DIPs: smooth at any size without thousands of points.
    private static int Segments(double length, double unit) => (int)Math.Clamp(length / (6 * unit), 4, 64);

    private static SketchPoint Wing(SketchPoint tip, double dx, double dy, double length, double degrees)
    {
        var angle = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        var (bx, by) = (-dx, -dy);
        return new SketchPoint(tip.X + (((bx * cos) - (by * sin)) * length), tip.Y + (((bx * sin) + (by * cos)) * length));
    }

    private static SketchPoint Offset(SketchPoint p, double nx, double ny, double by) => new(p.X + (nx * by), p.Y + (ny * by));

    private static SketchPoint Jitter(SketchPoint p, double amount, SketchRandom random) =>
        new(p.X + random.Range(-amount, amount), p.Y + random.Range(-amount, amount));

    private static void CheckUnit(double unit)
    {
        if (!double.IsFinite(unit) || unit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unit must be a positive number of pixels per DIP.");
        }
    }
}

/// <summary>Small deterministic generator (xorshift32): same seed, same sketch, on every runtime.</summary>
internal sealed class SketchRandom(int seed)
{
    private uint _state = seed == 0 ? 0x9E3779B9u : unchecked((uint)seed);

    public double Next()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x / 4294967296.0;
    }

    public double Range(double min, double max) => min + ((max - min) * Next());

    public int Sign() => Next() < 0.5 ? -1 : 1;
}
