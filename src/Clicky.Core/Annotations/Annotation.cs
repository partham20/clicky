using Clicky.Core.Actions;

namespace Clicky.Core.Annotations;

public enum AnnotationKind
{
    /// <summary>The pointer flies to a spot (point, or a step with a place on screen).</summary>
    Point,

    /// <summary>A glowing hand-drawn box around an area.</summary>
    Highlight,

    Arrow,

    Circle,

    Underline,

    Freehand,
}

/// <summary>Which side of its anchor a label sits on.</summary>
public enum LabelSide
{
    Right,
    Left,
    Above,
    Below,
}

/// <summary>
/// A mark ready to draw: strokes and anchors in physical screen pixels, already mapped from the
/// screenshot the model saw. Immutable; the renderer adds the timing.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="Strokes">The hand-drawn strokes, in drawing order. For a pointer, a small ring around the spot.</param>
/// <param name="Color">Pen colour.</param>
/// <param name="Unit">Physical pixels per DIP where the mark sits; used to size pens and labels.</param>
/// <param name="Target">For a pointer, the spot the tip lands on.</param>
/// <param name="Fill">For a highlight, the area to tint.</param>
/// <param name="Label">Short caption, or null.</param>
/// <param name="LabelAnchor">Where the label attaches.</param>
/// <param name="LabelSide">Which side of the anchor the label goes; the renderer keeps it on screen.</param>
public sealed record Annotation(
    AnnotationKind Kind,
    IReadOnlyList<SketchStroke> Strokes,
    DrawColor Color,
    double Unit,
    SketchPoint? Target = null,
    SketchBounds? Fill = null,
    string? Label = null,
    SketchPoint LabelAnchor = default,
    LabelSide LabelSide = LabelSide.Right)
{
    /// <summary>True for a walkthrough step marker; a new step replaces the previous one.</summary>
    public bool IsStep { get; init; }

    /// <summary>Total length of the strokes in physical pixels.</summary>
    public double InkLength => Strokes.Sum(s => s.Length);

    /// <summary>Everything the mark covers except its label, in physical pixels.</summary>
    public SketchBounds Bounds
    {
        get
        {
            var bounds = SketchBounds.Of(Strokes.SelectMany(s => s.Points));
            if (Strokes.Count == 0)
            {
                bounds = new SketchBounds(LabelAnchor.X, LabelAnchor.Y, LabelAnchor.X, LabelAnchor.Y);
            }

            if (Fill is { } fill)
            {
                bounds = bounds.Union(fill);
            }

            if (Target is { } target)
            {
                bounds = bounds.Union(new SketchBounds(target.X, target.Y, target.X, target.Y));
            }

            return bounds;
        }
    }
}
