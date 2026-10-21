namespace Clicky.Core.Capture;

/// <summary>
/// The media type of an encoded image, read from its first bytes. Screenshots are JPEG or PNG
/// depending on settings.capture.format, and history may hold both, so each one says what it is.
/// </summary>
public static class ImageMediaType
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string Webp = "image/webp";

    /// <summary>The type <paramref name="image"/>'s signature names; PNG when it is none of the four.</summary>
    public static string Of(ReadOnlySpan<byte> image)
    {
        if (image.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return Jpeg;
        }

        if (image.StartsWith("GIF8"u8))
        {
            return Gif;
        }

        if (image.Length >= 12 && image.StartsWith("RIFF"u8) && image[8..12].SequenceEqual("WEBP"u8))
        {
            return Webp;
        }

        return Png;
    }
}
