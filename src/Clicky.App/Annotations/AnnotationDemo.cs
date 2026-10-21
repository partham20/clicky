using Clicky.Core.Actions;
using Clicky.Core.Annotations;
using Clicky.Core.Geometry;
using Clicky.Core.Settings;
using Clicky.Platform.Windows.Display;
using Clicky.Platform.Windows.Input;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Annotations;

/// <summary>
/// Plays every kind of mark on the monitor under the pointer, through the same interface the talk
/// loop uses, with the coordinates a model would send for a screenshot of that monitor. For
/// checking the look on real hardware (tray › Drawing demo) until the talk loop drives it.
/// </summary>
public sealed class AnnotationDemo(IAnnotationRenderer renderer, ILogger<AnnotationDemo> logger)
{
    private int _running;

    /// <summary>Starts the demo; does nothing if one is already playing. UI thread.</summary>
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
            var monitors = DisplayMonitors.GetAll();
            var pointer = CursorReader.TryGet(out var position, out _) ? position : default;
            var monitor = CoordinateMapper.NearestMonitor(monitors, pointer);
            if (monitor is null)
            {
                logger.LogWarning("Drawing demo: no monitors");
                return;
            }

            // The geometry a default capture of this monitor would have.
            var (width, height) = CoordinateMapper.DownscaleSize(monitor.Bounds.Width, monitor.Bounds.Height, CaptureSettings.DefaultMaxLongEdge, CaptureSettings.DefaultMaxPixels);
            var geometry = new ScreenshotGeometry(monitor.Bounds, width, height);
            logger.LogInformation("Drawing demo on {Monitor}, screenshot {Geometry}", monitor, geometry);

            DrawPoint P(double x, double y) => new(x * width, y * height);
            ClickyAction[] script =
            [
                new PointAction(0.5 * width, 0.4 * height, "That's the Share button"),
                new HighlightAction(0.12 * width, 0.12 * height, 0.28 * width, 0.14 * height, "This toolbar has the formatting tools"),
                new DrawAction(DrawShape.Arrow, [P(0.72, 0.72), P(0.56, 0.5)], Label: "Click here"),
                new DrawAction(DrawShape.Circle, [P(0.3, 0.66)], Radius: 0.05 * width, Color: DrawColor.Red, Label: "This is the error"),
                new DrawAction(DrawShape.Underline, [P(0.6, 0.28), P(0.86, 0.28)], Color: DrawColor.Green),
                new DrawAction(DrawShape.Freehand, [P(0.1, 0.85), P(0.15, 0.8), P(0.2, 0.86), P(0.25, 0.8), P(0.3, 0.86)], Color: DrawColor.Yellow),
                new StepAction(2, "Open the File menu, then choose Export", Total: 4, X: 0.82 * width, Y: 0.55 * height),
            ];

            foreach (var action in script)
            {
                renderer.Show(action, geometry);
                await Task.Delay(TimeSpan.FromSeconds(1.1)).ConfigureAwait(true);
            }

            await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
            renderer.DismissAll();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Drawing demo failed");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
