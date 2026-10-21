using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Clicky.Platform.Windows.Capture;

/// <summary>Decodes a PNG to top-down BGRA pixels and back, with the WinRT codecs (free-threaded, no UI thread).</summary>
public static class PngPixels
{
    public static async Task<(byte[] Bgra, int Width, int Height)> DecodeAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(png.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
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

    public static async Task<byte[]> EncodeAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(cancellationToken).ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

        var png = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(png.AsBuffer(), (uint)png.Length, InputStreamOptions.None).AsTask(cancellationToken).ConfigureAwait(false);
        return png;
    }
}
