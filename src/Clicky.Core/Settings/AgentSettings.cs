namespace Clicky.Core.Settings;

/// <summary>settings.agent: computer use (M13).</summary>
/// <remarks>Positional so missing properties read as these defaults (see <see cref="CaptureSettings"/>).</remarks>
public sealed record AgentSettings(
    bool Enabled = true,
    int MaxSteps = AgentSettings.DefaultMaxSteps,
    int CancelWindowSeconds = AgentSettings.DefaultCancelWindowSeconds,
    bool ComputerToolset = true,
    bool UseBrowserProfile = true,
    string BrowserPath = "")
{
    public const int DefaultMaxSteps = 25;
    public const int MaxMaxSteps = 100;
    public const int DefaultCancelWindowSeconds = 5;
    public const int MaxCancelWindowSeconds = 30;

    /// <summary>Let Clicky act on the computer when asked. Off: click, type, key, scroll and open_url are refused.</summary>
    public bool Enabled { get; init; } = Enabled;

    /// <summary>Agent steps (act, look again) per request before Clicky stops and reports back.</summary>
    public int MaxSteps { get; init; } = MaxSteps;

    /// <summary>
    /// When Clicky is about to act without asking (you said "Always for this conversation"), the
    /// card counts down this long first so you can cancel. 0 turns the countdown off.
    /// </summary>
    public int CancelWindowSeconds { get; init; } = CancelWindowSeconds;

    /// <summary>
    /// API backend only: use Anthropic's computer toolset (what the Claude 5.x models are trained
    /// on) instead of Clicky's own click/type/key/scroll tools. Falls back by itself if the API refuses it.
    /// </summary>
    public bool ComputerToolset { get; init; } = ComputerToolset;

    /// <summary>open_url and browser work happen in Clicky's own Chrome (or Edge) profile, driven over CDP.</summary>
    public bool UseBrowserProfile { get; init; } = UseBrowserProfile;

    /// <summary>chrome.exe or msedge.exe to use; empty finds Chrome, then Edge.</summary>
    public string BrowserPath { get; init; } = BrowserPath;

    public AgentSettings Normalize() => this with
    {
        MaxSteps = Math.Clamp(MaxSteps, 1, MaxMaxSteps),
        CancelWindowSeconds = Math.Clamp(CancelWindowSeconds, 0, MaxCancelWindowSeconds),
        BrowserPath = BrowserPath ?? string.Empty,
    };
}
