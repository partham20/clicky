namespace Clicky.Core.Actions;

/// <summary>
/// Checks an action's coordinates against the screenshot it refers to. Models are sometimes a few
/// pixels off at an edge, so a point up to <see cref="Tolerance"/> outside the image is pulled onto
/// the edge; anything further out is rejected rather than guessed at.
/// </summary>
public static class ActionBounds
{
    public const double Tolerance = 8;

    /// <param name="width">Screenshot width in pixels.</param>
    /// <param name="height">Screenshot height in pixels.</param>
    /// <returns>The action (clamped onto the image if needed), or the reason it is off-screen.</returns>
    public static ActionParseResult Check(ClickyAction action, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var maxX = width - 1.0;
        var maxY = height - 1.0;

        bool Fits(double x, double y) => x <= maxX + Tolerance && y <= maxY + Tolerance;
        double ClampX(double x) => Math.Clamp(x, 0, maxX);
        double ClampY(double y) => Math.Clamp(y, 0, maxY);
        ActionParseResult Off(double x, double y) =>
            ActionParseResult.Invalid(action.Name, $"({x:0},{y:0}) is outside the {width}x{height} screenshot");

        switch (action)
        {
            case PointAction p:
                return Fits(p.X, p.Y) ? ActionParseResult.Valid(p with { X = ClampX(p.X), Y = ClampY(p.Y) }) : Off(p.X, p.Y);
            case ClickAction c:
                return Fits(c.X, c.Y) ? ActionParseResult.Valid(c with { X = ClampX(c.X), Y = ClampY(c.Y) }) : Off(c.X, c.Y);
            case ScrollAction s:
                return Fits(s.X, s.Y) ? ActionParseResult.Valid(s with { X = ClampX(s.X), Y = ClampY(s.Y) }) : Off(s.X, s.Y);
            case StepAction { X: { } x, Y: { } y } step:
                return Fits(x, y) ? ActionParseResult.Valid(step with { X = ClampX(x), Y = ClampY(y) }) : Off(x, y);
            case HighlightAction h:
                if (!Fits(h.X, h.Y))
                {
                    return Off(h.X, h.Y);
                }

                // Clip the box to the image; it keeps at least one pixel.
                var left = ClampX(h.X);
                var top = ClampY(h.Y);
                var right = Math.Clamp(h.X + h.Width, left + 1, width);
                var bottom = Math.Clamp(h.Y + h.Height, top + 1, height);
                return ActionParseResult.Valid(h with { X = left, Y = top, Width = right - left, Height = bottom - top });
            case DrawAction d:
                foreach (var point in d.Points)
                {
                    if (!Fits(point.X, point.Y))
                    {
                        return Off(point.X, point.Y);
                    }
                }

                return ActionParseResult.Valid(d with { Points = [.. d.Points.Select(p => new DrawPoint(ClampX(p.X), ClampY(p.Y)))] });
            default:
                return ActionParseResult.Valid(action);
        }
    }
}
