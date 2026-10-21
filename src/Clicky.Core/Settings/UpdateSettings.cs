namespace Clicky.Core.Settings;

/// <summary>settings.updates: how the installed app finds and fetches new versions.</summary>
/// <remarks>Positional so missing properties read as these defaults (see <see cref="CaptureSettings"/>).</remarks>
public sealed record UpdateSettings(bool AutoCheck = true, bool AutoDownload = true, string FeedUrl = "", bool Prerelease = false)
{
    /// <summary>Look for a new version shortly after startup and every few hours.</summary>
    public bool AutoCheck { get; init; } = AutoCheck;

    /// <summary>Download a new version in the background so Update only has to restart.</summary>
    public bool AutoDownload { get; init; } = AutoDownload;

    /// <summary>
    /// Empty: GitHub releases of the Clicky repository. Otherwise another GitHub repository URL, an
    /// https URL or a local folder holding Velopack's releases.win.json (for testing a release).
    /// </summary>
    public string FeedUrl { get; init; } = FeedUrl;

    /// <summary>Also take GitHub pre-releases.</summary>
    public bool Prerelease { get; init; } = Prerelease;

    public UpdateSettings Normalize() => this with { FeedUrl = FeedUrl?.Trim() ?? string.Empty };
}
