using Clicky.Core.Capture;
using Clicky.Core.Settings;

namespace Clicky.Tests.Assistant;

public sealed class ImageMediaTypeTests
{
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    [InlineData(new byte[] { 1, 2, 3 }, "image/png")]
    [InlineData(new byte[0], "image/png")]
    public void Reads_the_type_from_the_signature(byte[] image, string expected) => Assert.Equal(expected, ImageMediaType.Of(image));

    [Fact]
    public void Screenshots_default_to_jpeg_and_the_fast_route_to_no_thinking_with_a_warm_process()
    {
        var settings = ClickySettings.Default.Normalize();

        Assert.Equal(ScreenshotFormat.Jpeg, settings.Capture.Format);
        Assert.Equal(85, settings.Capture.JpegQuality);
        Assert.True(settings.Claude.KeepWarm);
        Assert.False(settings.Claude.ThinkingFor(Clicky.Core.Assistant.ModelTier.Fast));
        Assert.True(settings.Claude.ThinkingFor(Clicky.Core.Assistant.ModelTier.Deep));
        Assert.Equal(50, (settings.Capture with { JpegQuality = 3 }).Normalize().JpegQuality);
    }
}
