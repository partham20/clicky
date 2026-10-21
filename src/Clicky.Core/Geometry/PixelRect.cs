namespace Clicky.Core.Geometry;

/// <summary>A rectangle in physical screen pixels. Right and Bottom are exclusive, as in a Win32 RECT.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(PixelPoint point) =>
        point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

    /// <summary>Squared distance from <paramref name="point"/> to the nearest pixel inside; 0 when contained.</summary>
    public long DistanceSquaredTo(PixelPoint point)
    {
        long dx = point.X < Left ? Left - point.X : point.X >= Right ? point.X - (Right - 1) : 0;
        long dy = point.Y < Top ? Top - point.Y : point.Y >= Bottom ? point.Y - (Bottom - 1) : 0;
        return (dx * dx) + (dy * dy);
    }
}
