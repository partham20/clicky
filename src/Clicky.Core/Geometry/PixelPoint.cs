namespace Clicky.Core.Geometry;

/// <summary>
/// A point in physical screen pixels on the virtual desktop (origin at the primary monitor's
/// top-left, so monitors left of or above it have negative coordinates).
/// </summary>
public readonly record struct PixelPoint(int X, int Y);
