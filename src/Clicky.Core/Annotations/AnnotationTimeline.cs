namespace Clicky.Core.Annotations;

/// <summary>How a mark looks at one moment.</summary>
/// <param name="Flight">Eased 0 to 1: how far a pointer has flown from where it started.</param>
/// <param name="Draw">Eased 0 to 1: how much of the ink is down.</param>
/// <param name="LabelOpacity">0 to 1.</param>
/// <param name="Opacity">0 to 1 for the whole mark; falls to 0 as it fades out.</param>
/// <param name="IsAnimating">True while the mark changes from frame to frame.</param>
/// <param name="IsFinished">True once it has faded out and can be dropped.</param>
public readonly record struct AnnotationFrame(double Flight, double Draw, double LabelOpacity, double Opacity, bool IsAnimating, bool IsFinished);

/// <summary>
/// The life of one mark: fly (pointers only), draw in, hold, fade out. Dismissing it starts the fade
/// early. A pure function of time, so the renderer only needs frames while something is moving and
/// can sleep through the hold.
/// </summary>
public sealed class AnnotationTimeline
{
    public static readonly TimeSpan DefaultFade = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan LabelFade = TimeSpan.FromMilliseconds(150);

    // Pen speed for the draw-in, in DIPs per second, and the limits on how long it may take.
    private const double InkSpeed = 1400;
    private static readonly TimeSpan MinDraw = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxDraw = TimeSpan.FromMilliseconds(900);

    private readonly bool _labelOnLanding;

    /// <param name="start">When the mark appears, on the renderer's clock.</param>
    /// <param name="flight">How long a pointer flies; zero for other marks.</param>
    /// <param name="draw">How long the ink takes to go down.</param>
    /// <param name="hold">How long the finished mark stays; <see cref="Timeout.InfiniteTimeSpan"/> keeps it until dismissed.</param>
    /// <param name="fade">How long it takes to fade out.</param>
    /// <param name="labelOnLanding">Show the label as soon as the pointer lands rather than once the ink is down.</param>
    public AnnotationTimeline(TimeSpan start, TimeSpan flight, TimeSpan draw, TimeSpan hold, TimeSpan fade, bool labelOnLanding = false)
    {
        if (hold < TimeSpan.Zero && hold != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(hold), hold, "Hold must be zero or more, or infinite.");
        }

        Start = start;
        Flight = Max(flight, TimeSpan.Zero);
        Draw = Max(draw, TimeSpan.Zero);
        Hold = hold;
        Fade = Max(fade, TimeSpan.Zero);
        _labelOnLanding = labelOnLanding;
    }

    public TimeSpan Start { get; }

    public TimeSpan Flight { get; }

    public TimeSpan Draw { get; }

    public TimeSpan Hold { get; }

    public TimeSpan Fade { get; }

    public TimeSpan? DismissedAt { get; private set; }

    public TimeSpan Landed => Start + Flight;

    public TimeSpan Drawn => Landed + Draw;

    /// <summary>When the fade-out starts; <see cref="TimeSpan.MaxValue"/> while the mark is held until dismissed.</summary>
    public TimeSpan FadeStart
    {
        get
        {
            var natural = Hold == Timeout.InfiniteTimeSpan ? TimeSpan.MaxValue : Drawn + Hold;
            return DismissedAt is { } dismissed && dismissed < natural ? dismissed : natural;
        }
    }

    public TimeSpan End => FadeStart == TimeSpan.MaxValue ? TimeSpan.MaxValue : FadeStart + Fade;

    private TimeSpan LabelStart => _labelOnLanding ? Landed : Drawn;

    /// <summary>How long a mark's ink takes to draw at an even pen speed.</summary>
    public static TimeSpan DrawDuration(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var dips = annotation.InkLength / annotation.Unit;
        return Clamp(TimeSpan.FromSeconds(dips / InkSpeed), MinDraw, MaxDraw);
    }

    /// <summary>How long the pointer takes to fly <paramref name="distanceDips"/>; zero if it is practically there.</summary>
    public static TimeSpan FlightDuration(double distanceDips) =>
        distanceDips < 4 ? TimeSpan.Zero : Clamp(TimeSpan.FromSeconds(0.22 + (distanceDips / 3000)), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(650));

    /// <summary>Starts the fade-out now, unless it has already started. Returns false if it had.</summary>
    public bool Dismiss(TimeSpan now)
    {
        if (now >= FadeStart)
        {
            return false;
        }

        DismissedAt = now;
        return true;
    }

    public AnnotationFrame Evaluate(TimeSpan now)
    {
        var flight = EaseInOutCubic(Progress(now, Start, Flight));
        var draw = EaseOutQuad(Progress(now, Landed, Draw));
        var label = Progress(now, LabelStart, LabelFade);

        var fadeStart = FadeStart;
        var fading = now >= fadeStart;
        var opacity = fading ? 1 - EaseInQuad(Progress(now, fadeStart, Fade)) : 1;
        var finished = fading && now >= End;
        var animating = !finished && (fading || now < LabelStart + LabelFade || now < Drawn);
        return new AnnotationFrame(flight, draw, label, opacity, animating, finished);
    }

    private static double Progress(TimeSpan now, TimeSpan from, TimeSpan duration)
    {
        if (now < from)
        {
            return 0;
        }

        return duration <= TimeSpan.Zero ? 1 : Math.Min(1, (now - from) / duration);
    }

    private static double EaseInOutCubic(double t) => t < 0.5 ? 4 * t * t * t : 1 - (Math.Pow((-2 * t) + 2, 3) / 2);

    private static double EaseOutQuad(double t) => 1 - ((1 - t) * (1 - t));

    private static double EaseInQuad(double t) => t * t;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;
}

/// <summary>The path a pointer flies: a shallow hop from where it was to its target.</summary>
public static class PointerFlight
{
    /// <param name="progress">Eased 0 to 1.</param>
    public static SketchPoint PositionAt(SketchPoint from, SketchPoint to, double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        var distance = from.DistanceTo(to);
        if (distance <= 0)
        {
            return to;
        }

        // Control point lifted perpendicular to the path, on the upper side, so it arcs rather than slides.
        var middle = SketchPoint.Lerp(from, to, 0.5);
        var (nx, ny) = ((to.Y - from.Y) / distance, -(to.X - from.X) / distance);
        if (ny > 0)
        {
            (nx, ny) = (-nx, -ny);
        }

        var lift = distance * 0.15;
        var control = new SketchPoint(middle.X + (nx * lift), middle.Y + (ny * lift));
        var u = 1 - progress;
        return new SketchPoint(
            (u * u * from.X) + (2 * u * progress * control.X) + (progress * progress * to.X),
            (u * u * from.Y) + (2 * u * progress * control.Y) + (progress * progress * to.Y));
    }
}
