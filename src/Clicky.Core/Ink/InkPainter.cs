using Clicky.Core.Geometry;

namespace Clicky.Core.Ink;

/// <summary>
/// Paints the trail onto a copy of the screenshot's pixels: an anti-aliased pink line with a white
/// edge, so it reads on light and dark screens alike. Pure, so it is tested without Windows.
/// </summary>
public static class InkPainter
{
    /// <summary>The trail colour on screen and in screenshots: #DB2777, distinct from Clicky's own blue marks.</summary>
    public const byte InkRed = 0xDB;
    public const byte InkGreen = 0x27;
    public const byte InkBlue = 0x77;

    internal const double InkWidth = 2.5;
    internal const double HaloWidth = 5.5;
    internal const double InkAlpha = 0.95;
    internal const double HaloAlpha = 0.75;

    /// <summary>
    /// Draws <paramref name="strokes"/> (screenshot pixels, integers at pixel centres) into
    /// <paramref name="bgra"/>, a top-down 32-bit BGRA image with rows of <c>width * 4</c> bytes.
    /// Alpha is left alone. Parts off the image are clipped.
    /// </summary>
    public static void Paint(byte[] bgra, int width, int height, IReadOnlyList<IReadOnlyList<ScreenshotPoint>> strokes)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(strokes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgra.Length < (long)width * height * 4)
        {
            throw new ArgumentException("The pixel buffer is smaller than width x height x 4.", nameof(bgra));
        }

        const double reach = (HaloWidth / 2) + 1;
        var all = strokes.SelectMany(s => s).ToList();
        if (all.Count == 0)
        {
            return;
        }

        // Work only inside the trail's box, so a small mark on a big screenshot stays cheap.
        var box = InkRect.Of(all);
        var left = Math.Max(0, (int)Math.Floor(box.Left - reach));
        var top = Math.Max(0, (int)Math.Floor(box.Top - reach));
        var right = Math.Min(width - 1, (int)Math.Ceiling(box.Right + reach));
        var bottom = Math.Min(height - 1, (int)Math.Ceiling(box.Bottom + reach));
        if (left > right || top > bottom)
        {
            return;
        }

        var regionWidth = right - left + 1;
        var regionHeight = bottom - top + 1;
        var halo = new float[regionWidth * regionHeight];
        var ink = new float[regionWidth * regionHeight];

        // Coverage is the max over segments, so joints and overlaps are not painted twice.
        foreach (var stroke in strokes)
        {
            // A one-point stroke is a dot: a zero-length segment.
            var segments = Math.Max(1, stroke.Count - 1);
            for (var i = 0; i < segments && stroke.Count > 0; i++)
            {
                var a = stroke[i];
                var b = stroke[Math.Min(i + 1, stroke.Count - 1)];
                var x0 = Math.Max(left, (int)Math.Floor(Math.Min(a.X, b.X) - reach));
                var x1 = Math.Min(right, (int)Math.Ceiling(Math.Max(a.X, b.X) + reach));
                var y0 = Math.Max(top, (int)Math.Floor(Math.Min(a.Y, b.Y) - reach));
                var y1 = Math.Min(bottom, (int)Math.Ceiling(Math.Max(a.Y, b.Y) + reach));
                for (var y = y0; y <= y1; y++)
                {
                    var row = (y - top) * regionWidth;
                    for (var x = x0; x <= x1; x++)
                    {
                        var d = Polyline.DistanceToSegment(new ScreenshotPoint(x, y), a, b);
                        var index = row + x - left;
                        halo[index] = Math.Max(halo[index], Coverage(d, HaloWidth));
                        ink[index] = Math.Max(ink[index], Coverage(d, InkWidth));
                    }
                }
            }
        }

        for (var y = 0; y < regionHeight; y++)
        {
            for (var x = 0; x < regionWidth; x++)
            {
                var h = halo[(y * regionWidth) + x] * HaloAlpha;
                var k = ink[(y * regionWidth) + x] * InkAlpha;
                if (h <= 0 && k <= 0)
                {
                    continue;
                }

                var offset = ((((top + y) * width) + left + x) * 4);
                bgra[offset] = Blend(Blend(bgra[offset], 0xFF, h), InkBlue, k);
                bgra[offset + 1] = Blend(Blend(bgra[offset + 1], 0xFF, h), InkGreen, k);
                bgra[offset + 2] = Blend(Blend(bgra[offset + 2], 0xFF, h), InkRed, k);
            }
        }
    }

    /// <summary>How much of a pixel centred <paramref name="distance"/> from the line a pen of that width covers.</summary>
    private static float Coverage(double distance, double penWidth) => (float)Math.Clamp((penWidth / 2) + 0.5 - distance, 0, 1);

    private static byte Blend(byte under, byte over, double amount) =>
        (byte)Math.Clamp(Math.Round(under + ((over - under) * amount)), 0, 255);
}
