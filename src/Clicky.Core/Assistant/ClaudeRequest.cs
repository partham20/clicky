using Clicky.Core.Geometry;
using Clicky.Core.Ink;

namespace Clicky.Core.Assistant;

/// <summary>Which backend answers. Written to settings.json; do not rename.</summary>
public enum ClaudeBackendKind
{
    /// <summary>The Claude Messages API with the user's API key: fastest, billed per token.</summary>
    Api,

    /// <summary>Claude Code run headless (claude -p) on the user's Claude subscription: no API cost, slower to start.</summary>
    Subscription,
}

/// <summary>Written to settings.json; do not rename.</summary>
public enum ModelRouting
{
    /// <summary>The router picks fast or deep per question.</summary>
    Auto,

    AlwaysFast,

    AlwaysDeep,
}

public enum ModelTier
{
    /// <summary>Quick spoken questions: lowest latency.</summary>
    Fast,

    /// <summary>Explanations, teaching, long typed questions and agent work.</summary>
    Deep,
}

/// <summary>Thinking effort on the Messages API. Written to settings.json; do not rename.</summary>
public enum ClaudeEffort
{
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

/// <summary>How the question reached Clicky.</summary>
public enum RequestSource
{
    /// <summary>Held Talk and spoke; the text is a speech-to-text transcript.</summary>
    Voice,

    /// <summary>Typed into the text box.</summary>
    Typed,

    /// <summary>A step of a computer-use agent.</summary>
    Agent,
}

/// <param name="Png">The downscaled screenshot.</param>
/// <param name="Geometry">Its size and where it came from, so actions can be mapped back to the screen.</param>
/// <param name="Cursor">Where the user's pointer was, in screenshot pixels, if it was on the image.</param>
/// <param name="Ink">What the user did with the pointer while talking (set by IInkTrailService.AttachAsync).</param>
public sealed record RequestScreenshot(byte[] Png, ScreenshotGeometry Geometry, ScreenshotPoint? Cursor = null, SpatialInk? Ink = null);

/// <param name="Text">What the user said or typed.</param>
/// <param name="Tier">Forces the fast or deep model; null lets the router decide.</param>
/// <param name="Attachments">Documents and pictures to read with the question (text mode, M10); see <see cref="RequestAttachment"/>.</param>
public sealed record ClaudeRequest(
    string Text,
    RequestSource Source,
    RequestScreenshot? Screenshot = null,
    ModelTier? Tier = null,
    IReadOnlyList<RequestAttachment>? Attachments = null);
