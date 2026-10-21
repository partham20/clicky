using Clicky.Core.Geometry;

namespace Clicky.Core.Walkthroughs;

/// <summary>Where the user should act for a step, on the real screen.</summary>
/// <param name="Point">The spot the model pointed at, in physical pixels.</param>
/// <param name="Tolerance">How far from it a click still counts, in physical pixels.</param>
/// <param name="Monitor">Bounds of the monitor it is on.</param>
/// <param name="App">Process name of the app in front when the step was shown (without .exe), if known.</param>
public sealed record StepTarget(PixelPoint Point, int Tolerance, PixelRect Monitor, string? App = null);

public enum StepVerdict
{
    /// <summary>Not about this step.</summary>
    Ignore,

    /// <summary>The user did the step.</summary>
    Done,

    /// <summary>A click away from the spot; a few of these mean they went another way.</summary>
    Stray,

    /// <summary>The user did something else that changes the screen: take a new look.</summary>
    OffTrack,
}

/// <summary>Decides what one observation means for the step on screen. Pure, so the rules are testable.</summary>
public static class StepJudge
{
    // Shell surfaces that come and go around app switches (Start, search, the taskbar, toasts).
    // Switching to them is part of getting somewhere, not leaving the walkthrough.
    private static readonly HashSet<string> s_shell = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
        "ShellExperienceHost",
        "ShellHost",
        "TextInputHost",
        "LockApp",
        "?",
    };

    /// <param name="target">The step's spot, or null for a step with no place on screen (only the user can say it's done).</param>
    public static StepVerdict Judge(StepTarget? target, StepObservation observation, WalkthroughOptions options)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(options);
        if (target is null)
        {
            return StepVerdict.Ignore;
        }

        switch (observation)
        {
            case PointerClicked { OnClicky: true }:
                return StepVerdict.Ignore;

            case PointerClicked click:
                if (IsNear(click.At, target))
                {
                    return StepVerdict.Done;
                }

                // A big button: the model points at its middle, the user clicks its edge.
                return click.Element is { } under && Covers(under, target, options) ? StepVerdict.Done : StepVerdict.Stray;

            case ElementEvent element:
                if (Covers(element.Bounds, target, options))
                {
                    // Keyboard users: Tab to it, then Enter or Space; or an access key.
                    return element.Kind == ElementEventKind.MenuOpened ? StepVerdict.Ignore : StepVerdict.Done;
                }

                // Pressing some other button changes the screen; focus and selection move around too much to mean anything.
                return element.Kind == ElementEventKind.Invoke ? StepVerdict.OffTrack : StepVerdict.Ignore;

            case ForegroundChanged change:
                if (change.IsClicky || target.App is null || s_shell.Contains(change.App.App))
                {
                    return StepVerdict.Ignore;
                }

                return string.Equals(change.App.App, target.App, StringComparison.OrdinalIgnoreCase) ? StepVerdict.Ignore : StepVerdict.OffTrack;

            default:
                return StepVerdict.Ignore;
        }
    }

    /// <summary>True when <paramref name="point"/> is within the step's tolerance of its spot.</summary>
    public static bool IsNear(PixelPoint point, StepTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var dx = (long)point.X - target.Point.X;
        var dy = (long)point.Y - target.Point.Y;
        return (dx * dx) + (dy * dy) <= (long)target.Tolerance * target.Tolerance;
    }

    /// <summary>True when <paramref name="element"/> holds the spot and is small enough to be the thing there.</summary>
    public static bool Covers(PixelRect element, StepTarget target, WalkthroughOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        if (element.IsEmpty || !element.Contains(target.Point))
        {
            return false;
        }

        var monitor = target.Monitor;
        return element.Width <= Math.Max(1, monitor.Width * options.MaxElementShare)
            && element.Height <= Math.Max(1, monitor.Height * options.MaxElementShare);
    }

    /// <summary>The click tolerance in physical pixels for a step on a monitor at <paramref name="monitorScale"/>, from a screenshot at <paramref name="geometry"/>.</summary>
    public static int Tolerance(double monitorScale, ScreenshotGeometry geometry, WalkthroughOptions options)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(options);
        var fromDpi = options.ClickToleranceDip * monitorScale;
        var fromImage = options.ClickToleranceScreenshotPx / Math.Min(geometry.ScaleX, geometry.ScaleY);
        return (int)Math.Ceiling(Math.Max(fromDpi, fromImage));
    }
}
