namespace Clicky.Core.Settings;

/// <summary>The pointer trail recorded while Talk is held (M9), in settings.json under "ink".</summary>
public sealed record InkSettings(bool Enabled = true, bool ShowTrail = true, bool DrawOnScreenshot = true)
{
    /// <summary>Record the pointer during Talk and send it to Claude as spatial context.</summary>
    public bool Enabled { get; init; } = Enabled;

    /// <summary>Draw the trail on screen while Talk is held.</summary>
    public bool ShowTrail { get; init; } = ShowTrail;

    /// <summary>Paint the trail onto the screenshot sent to Claude, as well as describing it in words.</summary>
    public bool DrawOnScreenshot { get; init; } = DrawOnScreenshot;
}
