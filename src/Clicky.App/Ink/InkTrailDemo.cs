using Clicky.Core.Assistant;
using Clicky.Core.Buddy;
using Clicky.Core.Capture;
using Clicky.Core.Geometry;
using Clicky.Core.Ink;
using Clicky.Core.Settings;
using Clicky.Platform.Windows.Input;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Ink;

/// <summary>
/// Tray › Ink trail test: does what a Talk does with the pointer, without the voice. Takes the
/// screenshot, records the pointer for a few seconds with the trail on screen, then attaches it
/// and shows what Claude would be told. With settings.debug.saveCaptures on, the inked screenshot
/// is saved to the debug captures folder. For checking on real hardware until the talk loop (M6)
/// drives it.
/// </summary>
public sealed class InkTrailDemo(
    IInkTrailService ink,
    ICaptureService capture,
    IBuddyPresence presence,
    ISettingsStore settings,
    ILogger<InkTrailDemo> logger)
{
    private static readonly TimeSpan RecordFor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResultCaption = TimeSpan.FromSeconds(12);

    private int _running;

    /// <summary>Starts the test; does nothing if one is already running.</summary>
    public void Play()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            return;
        }

        _ = PlayAsync();
    }

    private async Task PlayAsync()
    {
        try
        {
            PixelPoint? cursor = CursorReader.TryGet(out var position, out _) ? position : null;
            var shot = capture.CaptureAsync(new CaptureTarget(settings.Current.Capture.Mode, cursor));
            presence.SetCaption($"Ink test: circle, underline or rest on something for {RecordFor.TotalSeconds:0} seconds");
            var recording = ink.Start();
            await Task.Delay(RecordFor).ConfigureAwait(false);
            var trail = recording.Finish();

            var result = await shot.ConfigureAwait(false);
            ScreenshotPoint? pointer = cursor is { } c ? CoordinateMapper.ToScreenshot(c, result.Geometry) : null;
            var attached = await ink.AttachAsync(new RequestScreenshot(result.Png, result.Geometry, pointer), trail).ConfigureAwait(false);
            var said = attached.Ink?.Describe() ?? "(the pointer barely moved over the screenshot, so nothing would be sent)";
            logger.LogInformation("Ink test on {Geometry}: {Description}", result.Geometry, said);
            presence.SetCaption(said);
            await Task.Delay(ResultCaption).ConfigureAwait(false);
            if (string.Equals(presence.Caption, said, StringComparison.Ordinal))
            {
                presence.SetCaption(null);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ink test failed");
            presence.SetCaption(null);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
