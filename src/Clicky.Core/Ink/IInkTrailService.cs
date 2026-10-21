using Clicky.Core.Assistant;

namespace Clicky.Core.Ink;

/// <summary>
/// Spatial context for Talk (M9): while the user holds Talk they can circle, underline or rest on
/// things with the mouse. The trail is drawn on the overlays as they go, and sent to the model
/// painted onto the screenshot and described as gestures in the prompt.
/// </summary>
/// <remarks>
/// <para>Use from the Talk loop: <see cref="Start"/> when Talk arms, <see cref="IInkRecording.Finish"/>
/// on release (or <see cref="IInkRecording.Cancel"/> if Talk is cancelled), then
/// <see cref="AttachAsync"/> on the screenshot taken when Talk armed, before sending the request.</para>
/// <para>All members are safe from any thread and never block on the UI thread.</para>
/// </remarks>
public interface IInkTrailService
{
    /// <summary>
    /// Starts recording the pointer (and drawing its trail, if settings.ink.showTrail is on). A
    /// recording still running is cancelled first. With settings.ink.enabled off this returns a
    /// recording that records nothing.
    /// </summary>
    IInkRecording Start();

    /// <summary>
    /// Adds the trail to the screenshot taken for the same Talk: reads it as gestures, paints it on
    /// a copy of the PNG (settings.ink.drawOnScreenshot) and sets <see cref="RequestScreenshot.Ink"/>,
    /// which the prompt then describes. Returns <paramref name="screenshot"/> unchanged when the
    /// pointer barely moved over it or ink is off. If the PNG cannot be painted the description is
    /// still attached.
    /// </summary>
    Task<RequestScreenshot> AttachAsync(RequestScreenshot screenshot, InkTrail trail, CancellationToken cancellationToken = default);
}

/// <summary>One Talk's pointer recording.</summary>
public interface IInkRecording : IDisposable
{
    /// <summary>
    /// Stops recording and returns the trail. The ink on screen holds briefly, then fades. Calling
    /// it again returns the same trail. Dispose does the same without returning it.
    /// </summary>
    InkTrail Finish();

    /// <summary>Stops recording and wipes the ink at once (Talk cancelled).</summary>
    void Cancel();
}
