namespace Clicky.Core.Geometry;

/// <summary>A point in device-independent pixels (1/96 inch) relative to some monitor's top-left.</summary>
public readonly record struct DipPoint(double X, double Y);
