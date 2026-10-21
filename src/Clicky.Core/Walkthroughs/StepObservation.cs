using Clicky.Core.Actions;
using Clicky.Core.Geometry;
using Clicky.Core.Skills;

namespace Clicky.Core.Walkthroughs;

/// <summary>Something the user did that may finish the current step. Reported by <see cref="IStepWatcher"/>.</summary>
public abstract record StepObservation;

/// <summary>A mouse button went down and came up again.</summary>
/// <param name="At">Where it went down, in physical pixels.</param>
/// <param name="Element">The accessible element under the click, in physical pixels, when it could be read.</param>
/// <param name="OnClicky">It landed on one of Clicky's own windows (the step card): not part of the step.</param>
public sealed record PointerClicked(PixelPoint At, MouseButton Button, PixelRect? Element = null, bool OnClicky = false) : StepObservation;

public enum ElementEventKind
{
    /// <summary>Keyboard focus moved to the element (clicked, tabbed to, or opened by a shortcut).</summary>
    Focus,

    /// <summary>The element was invoked (a button pressed, by mouse, keyboard or access key).</summary>
    Invoke,

    /// <summary>The element became the selection (a tab, list item or radio button).</summary>
    Select,

    /// <summary>A menu or drop-down opened; the bounds are the popup's.</summary>
    MenuOpened,
}

/// <summary>An accessibility event in another app.</summary>
/// <param name="Bounds">The element, in physical pixels.</param>
/// <param name="Name">Its accessible name, for the log.</param>
public sealed record ElementEvent(ElementEventKind Kind, PixelRect Bounds, string? Name = null) : StepObservation;

/// <summary>Another window came to the front.</summary>
/// <param name="IsClicky">One of Clicky's own windows.</param>
public sealed record ForegroundChanged(ForegroundApp App, bool IsClicky = false) : StepObservation;

/// <summary>
/// Watches what the user does with the mouse and keyboard focus while a walkthrough waits for them.
/// The Windows one holds a low-level mouse hook and accessibility event hooks; they cost something
/// on every mouse event system-wide, so they exist only between <see cref="Start"/> and disposing
/// what it returns.
/// </summary>
public interface IStepWatcher
{
    /// <summary>
    /// Starts reporting. <paramref name="observer"/> runs on a watcher thread, must be quick and must
    /// not throw. Dispose the result to stop and remove the hooks; it may block briefly.
    /// </summary>
    IDisposable Start(Action<StepObservation> observer);
}
