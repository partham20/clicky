using System.Runtime.InteropServices.WindowsRuntime;
using Clicky.Core.Capture;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Clicky.Platform.Windows.Capture;

/// <summary>
/// Decodes a screenshot (JPEG or PNG) to top-down BGRA pixels and encodes pixels back, optionally
/// scaled, with the WinRT codecs (free-threaded, no UI thread).
/// </summary>
public static class ScreenshotCodec
{
    public static async Task<(byte[] Bgra, int Width, int Height)> DecodeAsync(byte[] image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(image.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken).ConfigureAwait(false);
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);
        return (data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    /// <summary>Encodes <paramref name="bgra"/> as a JPEG or PNG, scaled to the output size with Fant when it differs.</summary>
    /// <param name="jpegQuality">1-100; ignored for PNG.</param>
    public static async Task<byte[]> EncodeAsync(
        byte[] bgra,
        int width,
        int height,
        ScreenshotFormat format,
        int jpegQuality,
        int outputWidth = 0,
        int outputHeight = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = format == ScreenshotFormat.Jpeg
            ? await CreateJpegEncoderAsync(stream, jpegQuality, cancellationToken).ConfigureAwait(false)
            : await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(cancellationToken).ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        if (outputWidth > 0 && outputHeight > 0 && (outputWidth != width || outputHeight != height))
        {
            encoder.BitmapTransform.ScaledWidth = (uint)outputWidth;
            encoder.BitmapTransform.ScaledHeight = (uint)outputHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static async Task<BitmapEncoder> CreateJpegEncoderAsync(InMemoryRandomAccessStream stream, int quality, CancellationToken cancellationToken)
    {
        try
        {
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(Math.Clamp(quality, 1, 100) / 100f, PropertyType.Single) };
            return await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or System.Runtime.InteropServices.COMException)
        {
            // A codec that rejects the quality option still encodes at its default (about 90).
            return await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream).AsTask(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The format <paramref name="image"/> is in, so an edited copy can be written back the same way.</summary>
    public static ScreenshotFormat FormatOf(byte[] image) =>
        ImageMediaType.Of(image) == ImageMediaType.Jpeg ? ScreenshotFormat.Jpeg : ScreenshotFormat.Png;
}
