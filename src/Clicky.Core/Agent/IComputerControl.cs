using Clicky.Core.Actions;
using Clicky.Core.Geometry;

namespace Clicky.Core.Agent;

/// <summary>
/// Does computer-control actions (click, type, key, scroll, open_url) on the real desktop. The
/// Windows implementation prefers UI Automation patterns, so the user's pointer doesn't move, and
/// drives Clicky's own browser profile over CDP; it moves the real mouse only when told it may.
/// </summary>
/// <remarks>Thread-safe. Never touches the UI thread.</remarks>
public interface IComputerControl
{
    /// <summary>
    /// What <paramref name="action"/> would act on (the control under a click, the focused field for
    /// typing), so the permission gate can name it and spot a Send or Delete button. Never throws
    /// for an app that doesn't answer; returns <see cref="ControlTarget.Unknown"/>.
    /// </summary>
    /// <param name="geometry">Maps the action's screenshot coordinates to the screen; null if there was no screenshot.</param>
    Task<ControlTarget> InspectAsync(ClickyAction action, ScreenshotGeometry? geometry, CancellationToken cancellationToken = default);

    /// <summary>Runs one action. Problems come back as outcomes, not exceptions; only cancellation throws.</summary>
    Task<ControlOutcome> ExecuteAsync(ClickyAction action, ScreenshotGeometry? geometry, ControlOptions options, CancellationToken cancellationToken = default);
}

/// <param name="Name">The control's accessible name, e.g. "Send".</param>
/// <param name="Role">Its kind, e.g. "button", "edit".</param>
/// <param name="App">The app's name, e.g. "Outlook" or the process name.</param>
/// <param name="Process">Process file name without extension, lowercase, e.g. "olk".</param>
/// <param name="Elevated">It runs as administrator and Clicky doesn't, so Windows won't let Clicky reach it.</param>
public sealed record ControlTarget(string? Name, string? Role, string? App, string? Process = null, bool Elevated = false)
{
    public static ControlTarget Unknown { get; } = new(null, null, null);
}

public enum ControlStatus
{
    Done,

    /// <summary>Could not do it without moving the user's mouse (no UI Automation pattern); ask, then retry with <see cref="ControlOptions.AllowMouse"/>.</summary>
    NeedsMouse,

    /// <summary>Tried and it didn't work.</summary>
    Failed,

    /// <summary>Not allowed to try: an administrator window, or a secure desktop.</summary>
    Blocked,
}

/// <param name="Detail">One sentence for the model: what was done and how, or what went wrong.</param>
public sealed record ControlOutcome(ControlStatus Status, string Detail)
{
    public static ControlOutcome Done(string detail) => new(ControlStatus.Done, detail);

    public static ControlOutcome Failed(string detail) => new(ControlStatus.Failed, detail);
}

/// <param name="AllowMouse">The user agreed to Clicky moving the real pointer (SendInput) when UI Automation can't do it.</param>
public sealed record ControlOptions(bool AllowMouse = false);
