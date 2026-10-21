using Clicky.Core.Actions;
using Clicky.Core.Geometry;

namespace Clicky.Core.Annotations;

/// <param name="Hold">
/// How long the finished mark stays before fading. Null uses <see cref="DefaultHold"/>;
/// <see cref="Timeout.InfiniteTimeSpan"/> keeps it until it is dismissed.
/// </param>
/// <param name="PointerFrom">
/// Where a pointer starts its flight, in physical pixels. Null flies it from the pointer mark already
/// showing, or else from the mouse pointer (where the buddy is).
/// </param>
public sealed record AnnotationOptions(TimeSpan? Hold = null, PixelPoint? PointerFrom = null)
{
    public static readonly TimeSpan PointerHold = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan MarkHold = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StepHold = TimeSpan.FromSeconds(60);

    /// <summary>How long a mark stays once drawn when no hold is given.</summary>
    public static TimeSpan DefaultHold(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return annotation.IsStep ? StepHold : annotation.Kind == AnnotationKind.Point ? PointerHold : MarkHold;
    }
}

/// <summary>A mark on screen, for taking it down before its time is up.</summary>
public interface IAnnotation
{
    /// <summary>Fades the mark out. Safe from any thread and more than once.</summary>
    void Dismiss();
}

/// <summary>
/// Draws point, highlight, draw and step actions on the overlays in a hand-drawn marker style:
/// pointers fly to their spot, ink draws itself in, labels pop up beside it, and everything fades
/// after a while. Renders frames only while something moves.
/// </summary>
/// <remarks>All members are safe from any thread; the work happens on the UI thread.</remarks>
public interface IAnnotationRenderer
{
    /// <summary>
    /// Puts an action on screen. Coordinates are pixels of the screenshot described by
    /// <paramref name="geometry"/>; they are checked against it and mapped to the screen here.
    /// </summary>
    /// <returns>The mark, or null for an action that draws nothing (say, click, a step without x and y...).</returns>
    IAnnotation? Show(ClickyAction action, ScreenshotGeometry geometry, AnnotationOptions? options = null);

    /// <summary>Fades out everything. Call when a new question starts or the user cancels.</summary>
    void DismissAll();

    /// <summary>Removes everything at once, without a fade.</summary>
    void Clear();
}
