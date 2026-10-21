using Clicky.Core.Actions;
using Clicky.Core.Geometry;

namespace Clicky.Core.Annotations;

/// <summary>
/// Turns a point, highlight, draw or step action into an <see cref="Annotation"/> on the real screen:
/// checks it against the screenshot it refers to, maps screenshot pixels to physical pixels, and
/// sketches the strokes.
/// </summary>
public static class AnnotationPlanner
{
    /// <summary>Radius of the ring drawn around the spot a pointer lands on, in DIPs.</summary>
    public const double PointerRingRadius = 18;

    /// <summary>Gap between a highlighted area and its box, in DIPs, so the ink does not cover what it marks.</summary>
    public const double HighlightPadding = 4;

    /// <summary>Longest label shown; longer step instructions are cut with an ellipsis.</summary>
    public const int MaxLabelLength = 90;

    // Room a label usually needs beside its anchor before it goes on the other side, in DIPs.
    private const double LabelRoomX = 260;
    private const double LabelRoomY = 44;

    // Radius used when a circle comes without one (the schema requires it, models forget), in screenshot pixels.
    private const double DefaultCircleRadius = 30;

    /// <summary>True for actions that put something on screen.</summary>
    public static bool Draws(ClickyAction action) =>
        action is PointAction or HighlightAction or DrawAction or StepAction { X: not null, Y: not null };

    /// <param name="action">A point, highlight, draw or step action in screenshot pixels.</param>
    /// <param name="geometry">The screenshot the coordinates refer to.</param>
    /// <param name="unitAt">Physical pixels per DIP at a physical position (the scale of the monitor there).</param>
    /// <param name="seed">Seeds the hand-drawn wobble.</param>
    /// <param name="annotation">The mark to draw.</param>
    /// <param name="error">Why nothing will be drawn, for the log.</param>
    public static bool TryPlan(
        ClickyAction action,
        ScreenshotGeometry geometry,
        Func<SketchPoint, double> unitAt,
        int seed,
        out Annotation? annotation,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(unitAt);
        annotation = null;

        if (!Draws(action))
        {
            error = $"{action.Name} draws nothing";
            return false;
        }

        var checkedAction = ActionBounds.Check(action, geometry.ImageWidth, geometry.ImageHeight);
        if (checkedAction.Action is null)
        {
            error = checkedAction.Error;
            return false;
        }

        var map = new Mapping(geometry, unitAt);
        error = null;
        switch (checkedAction.Action)
        {
            case PointAction p:
                annotation = Pointer(map, map.Centre(p.X, p.Y), p.Label, seed);
                return true;

            case StepAction { X: { } x, Y: { } y } step:
                annotation = Pointer(map, map.Centre(x, y), StepLabel(step), seed) with { IsStep = true };
                return true;

            case HighlightAction h:
                annotation = Highlight(map, h, seed);
                return true;

            case DrawAction d:
                annotation = Draw(map, d, seed, out error);
                return annotation is not null;

            default:
                error = $"{action.Name} draws nothing";
                return false;
        }
    }

    private static Annotation Pointer(Mapping map, SketchPoint target, string? label, int seed)
    {
        var unit = map.UnitAt(target);
        var ring = RoughSketch.Ellipse(target, PointerRingRadius * unit, PointerRingRadius * unit, unit, seed);

        // The arrow glyph hangs down and right of its tip; the label goes beside it.
        var onLeft = target.X + (LabelRoomX * unit) > map.Source.Right;
        var anchor = onLeft
            ? new SketchPoint(target.X - ((PointerRingRadius + 4) * unit), target.Y)
            : new SketchPoint(target.X + (26 * unit), target.Y + (14 * unit));
        return new Annotation(
            AnnotationKind.Point,
            ring,
            DrawColor.Accent,
            unit,
            Target: target,
            Label: Clean(label),
            LabelAnchor: anchor,
            LabelSide: onLeft ? LabelSide.Left : LabelSide.Right);
    }

    private static Annotation Highlight(Mapping map, HighlightAction h, int seed)
    {
        var topLeft = map.Edge(h.X, h.Y);
        var bottomRight = map.Edge(h.X + h.Width, h.Y + h.Height);
        var unit = map.UnitAt(SketchPoint.Lerp(topLeft, bottomRight, 0.5));
        var pad = HighlightPadding * unit;
        var source = map.Source;
        var box = new SketchBounds(
            Math.Max(topLeft.X - pad, source.Left),
            Math.Max(topLeft.Y - pad, source.Top),
            Math.Min(bottomRight.X + pad, source.Right),
            Math.Min(bottomRight.Y + pad, source.Bottom));

        // Above the box if there is room, else below it, else just inside its top-left corner.
        var gap = 8 * unit;
        var (anchor, side) = box.Top - (LabelRoomY * unit) >= source.Top
            ? (new SketchPoint(box.Left, box.Top - gap), LabelSide.Above)
            : box.Bottom + (LabelRoomY * unit) <= source.Bottom
                ? (new SketchPoint(box.Left, box.Bottom + gap), LabelSide.Below)
                : (new SketchPoint(box.Left + gap, box.Top + gap), LabelSide.Below);

        return new Annotation(
            AnnotationKind.Highlight,
            RoughSketch.Rectangle(box, unit, seed),
            DrawColor.Accent,
            unit,
            Fill: box,
            Label: Clean(h.Label),
            LabelAnchor: anchor,
            LabelSide: side);
    }

    private static Annotation? Draw(Mapping map, DrawAction d, int seed, out string? error)
    {
        var points = d.Points.Select(p => map.Centre(p.X, p.Y)).ToArray();
        var color = d.Color ?? DrawColor.Accent;
        var label = Clean(d.Label);
        error = null;

        if (points.Length == 0)
        {
            error = "draw has no points";
            return null;
        }

        if (d.Shape != DrawShape.Circle && points.Length < 2)
        {
            error = $"a {d.Shape.ToString().ToLowerInvariant()} needs at least two points";
            return null;
        }

        var first = points[0];
        var last = points[^1];
        var unit = map.UnitAt(d.Shape == DrawShape.Circle ? first : SketchPoint.Lerp(first, last, 0.5));
        var gap = 8 * unit;
        var source = map.Source;

        switch (d.Shape)
        {
            case DrawShape.Arrow:
            {
                // The label goes at the tail, on the side away from where the arrow heads.
                var (dx, dy) = (last.X - first.X, last.Y - first.Y);
                var side = Math.Abs(dx) >= Math.Abs(dy)
                    ? dx >= 0 ? LabelSide.Left : LabelSide.Right
                    : dy >= 0 ? LabelSide.Above : LabelSide.Below;
                var anchor = side switch
                {
                    LabelSide.Left => new SketchPoint(first.X - gap, first.Y),
                    LabelSide.Right => new SketchPoint(first.X + gap, first.Y),
                    LabelSide.Above => new SketchPoint(first.X, first.Y - gap),
                    _ => new SketchPoint(first.X, first.Y + gap),
                };
                return new Annotation(AnnotationKind.Arrow, RoughSketch.Arrow(first, last, unit, seed), color, unit, Label: label, LabelAnchor: anchor, LabelSide: side);
            }

            case DrawShape.Circle:
            {
                var radius = map.Length(d.Radius ?? DefaultCircleRadius);
                var maxRadius = Math.Max(source.Width, source.Height);
                radius = Math.Clamp(radius, 4 * unit, maxRadius);
                var onLeft = first.X + radius + (LabelRoomX * unit) > source.Right;
                var anchor = onLeft ? new SketchPoint(first.X - radius - gap, first.Y) : new SketchPoint(first.X + radius + gap, first.Y);
                return new Annotation(
                    AnnotationKind.Circle,
                    RoughSketch.Ellipse(first, radius, radius, unit, seed),
                    color,
                    unit,
                    Label: label,
                    LabelAnchor: anchor,
                    LabelSide: onLeft ? LabelSide.Left : LabelSide.Right);
            }

            case DrawShape.Underline:
                // Below the line's left end, clear of the return stroke.
                return new Annotation(
                    AnnotationKind.Underline,
                    RoughSketch.Underline(first, last, unit, seed),
                    color,
                    unit,
                    Label: label,
                    LabelAnchor: new SketchPoint(Math.Min(first.X, last.X), Math.Max(first.Y, last.Y) + (gap * 1.5)),
                    LabelSide: LabelSide.Below);

            default:
                return new Annotation(
                    AnnotationKind.Freehand,
                    RoughSketch.Freehand(points, unit, seed),
                    color,
                    unit,
                    Label: label,
                    LabelAnchor: new SketchPoint(last.X + gap, last.Y),
                    LabelSide: LabelSide.Right);
        }
    }

    private static string StepLabel(StepAction step)
    {
        var prefix = step.Total is { } total ? $"Step {step.Index} of {total}: " : $"Step {step.Index}: ";
        return Clean(prefix + step.Instruction)!;
    }

    private static string? Clean(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        label = string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return label.Length <= MaxLabelLength ? label : string.Concat(label.AsSpan(0, MaxLabelLength - 1).TrimEnd(), "…");
    }

    /// <summary>Screenshot pixels to physical pixels, kept fractional so sketches stay smooth.</summary>
    private sealed class Mapping(ScreenshotGeometry geometry, Func<SketchPoint, double> unitAt)
    {
        public SketchBounds Source { get; } = new(geometry.Source.Left, geometry.Source.Top, geometry.Source.Right, geometry.Source.Bottom);

        /// <summary>
        /// A screenshot pixel centre to the matching physical position: the inverse of
        /// <see cref="CoordinateMapper.ToScreenshot"/>, clamped to the captured area.
        /// </summary>
        public SketchPoint Centre(double x, double y) => new(
            Math.Clamp(Source.Left + ((x + 0.5) / geometry.ScaleX) - 0.5, Source.Left, Source.Right - 1),
            Math.Clamp(Source.Top + ((y + 0.5) / geometry.ScaleY) - 0.5, Source.Top, Source.Bottom - 1));

        /// <summary>A screenshot pixel edge (as in a box's x, y, width, height) to a physical pixel edge.</summary>
        public SketchPoint Edge(double x, double y) => new(
            Math.Clamp(Source.Left + (x / geometry.ScaleX), Source.Left, Source.Right),
            Math.Clamp(Source.Top + (y / geometry.ScaleY), Source.Top, Source.Bottom));

        public double Length(double screenshotPixels) => screenshotPixels / Math.Min(geometry.ScaleX, geometry.ScaleY);

        public double UnitAt(SketchPoint point)
        {
            var unit = unitAt(point);
            return double.IsFinite(unit) && unit > 0 ? unit : 1;
        }
    }
}
