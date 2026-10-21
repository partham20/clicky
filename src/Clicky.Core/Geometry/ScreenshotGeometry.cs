namespace Clicky.Core.Geometry;

/// <summary>
/// A point in screenshot pixels. Integer values are pixel centres: (0,0) is the centre of the
/// top-left pixel, which is how a model reading the image reports coordinates.
/// </summary>
public readonly record struct ScreenshotPoint(double X, double Y);

/// <summary>Where a screenshot came from and how it was scaled.</summary>
/// <param name="Source">The captured screen area in physical pixels (virtual-desktop coordinates).</param>
/// <param name="ImageWidth">Width of the encoded image after downscaling.</param>
/// <param name="ImageHeight">Height of the encoded image after downscaling.</param>
public sealed record ScreenshotGeometry
{
    public ScreenshotGeometry(PixelRect source, int imageWidth, int imageHeight)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Screenshot source must not be empty.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageHeight);
        Source = source;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
    }

    public PixelRect Source { get; }

    public int ImageWidth { get; }

    public int ImageHeight { get; }

    /// <summary>Screenshot pixels per physical pixel horizontally; at most 1 (we never upscale).</summary>
    public double ScaleX => (double)ImageWidth / Source.Width;

    public double ScaleY => (double)ImageHeight / Source.Height;

    public override string ToString() =>
        $"{Source.Width}x{Source.Height} at ({Source.Left},{Source.Top}) -> {ImageWidth}x{ImageHeight}";
}
