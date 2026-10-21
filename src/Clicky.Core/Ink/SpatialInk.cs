using System.Globalization;
using System.Text;
using Clicky.Core.Geometry;

namespace Clicky.Core.Ink;

/// <summary>A box in screenshot pixels.</summary>
public readonly record struct InkRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public ScreenshotPoint Centre => new((Left + Right) / 2, (Top + Bottom) / 2);

    public static InkRect Of(IEnumerable<ScreenshotPoint> points)
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

        return left > right ? default : new InkRect(left, top, right, bottom);
    }

    public InkRect Union(InkRect other) =>
        new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    public bool Contains(ScreenshotPoint p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    /// <summary>Area of the overlap divided by the smaller box's area: 1 when one box sits inside the other.</summary>
    public double OverlapOfSmaller(InkRect other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        var smaller = Math.Min(Width * Height, other.Width * other.Height);
        return smaller <= 0 ? 0 : w * h / smaller;
    }
}

/// <summary>What the user seemed to do with the pointer. Order of the enum means nothing.</summary>
public enum InkGestureKind
{
    /// <summary>Drew a loop around something (once or several times).</summary>
    Circle,

    /// <summary>Swept along under something, left to right or back and forth.</summary>
    Underline,

    /// <summary>Held the pointer still on something.</summary>
    Pause,

    /// <summary>Moved straight from one place to another.</summary>
    Move,

    /// <summary>Any other movement.</summary>
    Path,
}

/// <summary>One thing the user did with the pointer, in screenshot pixels.</summary>
/// <param name="Points">
/// Circle: none needed (see <paramref name="Bounds"/>). Underline: left end, right end. Pause: the
/// spot. Move: from, to. Path: up to <see cref="InkAnalyzer.MaxPathPoints"/> points along it.
/// </param>
/// <param name="Start">When it started, from the start of the recording.</param>
/// <param name="Duration">How long it took (for a pause, how long the pointer rested).</param>
public sealed record InkGesture(InkGestureKind Kind, InkRect Bounds, IReadOnlyList<ScreenshotPoint> Points, TimeSpan Start, TimeSpan Duration)
{
    /// <summary>A short phrase for the prompt, e.g. "circled the area (412, 220) to (560, 301)".</summary>
    public string Describe() => Kind switch
    {
        InkGestureKind.Circle => string.Create(CultureInfo.InvariantCulture, $"circled the area {P(Bounds.Left, Bounds.Top)} to {P(Bounds.Right, Bounds.Bottom)}"),
        InkGestureKind.Underline => $"underlined from {P(Points[0])} to {P(Points[^1])}",
        InkGestureKind.Pause => string.Create(CultureInfo.InvariantCulture, $"rested the pointer on {P(Points[0])} for {Duration.TotalSeconds:0.0} s"),
        InkGestureKind.Move => $"moved from {P(Points[0])} to {P(Points[^1])}",
        _ => $"traced a path through {string.Join(", ", Points.Select(P))}",
    };

    private static string P(ScreenshotPoint p) => P(p.X, p.Y);

    private static string P(double x, double y) => string.Create(CultureInfo.InvariantCulture, $"({x:0}, {y:0})");
}

/// <summary>
/// The pointer trail of one Talk, mapped onto its screenshot and read as gestures: the spatial
/// context sent to the model alongside the screenshot.
/// </summary>
/// <param name="Gestures">In the order the user made them.</param>
/// <param name="Strokes">The trail's parts that fall on the screenshot, simplified, for drawing on it.</param>
/// <param name="DrawnOnScreenshot">True once <see cref="Strokes"/> are painted onto the screenshot sent with it.</param>
public sealed record SpatialInk(IReadOnlyList<InkGesture> Gestures, IReadOnlyList<IReadOnlyList<ScreenshotPoint>> Strokes, bool DrawnOnScreenshot = false)
{
    /// <summary>The colour the trail is drawn in, on screen and on the screenshot, as the prompt names it.</summary>
    public const string ColourName = "pink";

    /// <summary>One bracketed line for the user's turn, matching the screenshot line before it.</summary>
    public string Describe()
    {
        var text = new StringBuilder("[While talking, the user moved their pointer to show you something");
        text.Append(DrawnOnScreenshot
            ? $"; its trail is drawn on the screenshot as a {ColourName} line with a white edge, which is not part of the screen. In order, they "
            : ". In order, they ");
        text.AppendJoin("; then ", Gestures.Select(g => g.Describe()));
        return text.Append(".]").ToString();
    }
}
