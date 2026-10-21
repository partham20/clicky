using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clicky.Core.Assistant;
using Clicky.Core.Documents;
using Clicky.Core.Settings;

namespace Clicky.Platform.Windows.Documents;

/// <summary>
/// Loads pictures the user drops through WIC (any format Windows can decode: PNG, JPEG, GIF, BMP,
/// TIFF, and WebP/HEIC with their codecs installed). A picture already within the screenshot caps
/// and the API's formats goes as it is; anything else is scaled down to the caps and re-encoded,
/// JPEG for photos and PNG otherwise, in memory only.
/// </summary>
public sealed class WicImageLoader(ISettingsStore settings) : IImageLoader
{
    /// <summary>Base64 grows this to about 5 MB, the API's limit per image.</summary>
    public const int MaxEncodedBytes = 3_700_000;

    public const long MaxFileBytes = 100L * 1024 * 1024;

    private static readonly HashSet<string> s_sendable = new(StringComparer.Ordinal) { "image/png", "image/jpeg", "image/gif", "image/webp" };

    public Task<RequestAttachment> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var caps = settings.Current.Capture;
        return Task.Run(() => Load(path, caps.MaxLongEdge, caps.MaxPixels), cancellationToken);
    }

    private static RequestAttachment Load(string path, int maxLongEdge, int maxPixels)
    {
        var name = Path.GetFileName(path);
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                throw new DocumentException($"{name} is too big ({info.Length / (1024 * 1024)} MB).");
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DocumentException($"Clicky can't open {name}: {ex.Message}", ex);
        }

        BitmapSource frame;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            throw new DocumentException($"Windows can't open {name} as a picture.", ex);
        }

        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        var scale = Math.Min(1.0, Math.Min((double)maxLongEdge / Math.Max(width, height), Math.Sqrt((double)maxPixels / ((double)width * height))));
        var mediaType = DocumentFileReader.ImageMediaType(path) ?? "image/png";
        if (scale >= 1.0 && s_sendable.Contains(mediaType) && bytes.Length <= MaxEncodedBytes)
        {
            return RequestAttachment.Picture(name, bytes, mediaType);
        }

        BitmapSource scaled = scale < 1.0 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
        var photo = mediaType == "image/jpeg";
        var encoded = Encode(scaled, jpeg: photo);
        if (!photo && encoded.Length > MaxEncodedBytes)
        {
            (encoded, photo) = (Encode(scaled, jpeg: true), true);
        }

        if (encoded.Length > MaxEncodedBytes)
        {
            throw new DocumentException($"{name} is still too big after scaling it down.");
        }

        return RequestAttachment.Picture(name, encoded, photo ? "image/jpeg" : "image/png");
    }

    private static byte[] Encode(BitmapSource image, bool jpeg)
    {
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 88 } : new PngBitmapEncoder();

        // JPEG has no alpha; flatten anything else to 24-bit so transparent areas don't turn black at random.
        var source = jpeg && image.Format != PixelFormats.Bgr24 ? new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0) : image;
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
