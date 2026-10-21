namespace Clicky.Core.Updates;

public enum UpdatePhase
{
    /// <summary>Development build: updates only work in the installed app.</summary>
    NotInstalled,

    /// <summary>Nothing checked yet this session.</summary>
    Idle,

    Checking,

    UpToDate,

    /// <summary>A newer version exists and automatic download is off.</summary>
    Available,

    Downloading,

    /// <summary>Downloaded; Update restarts into it.</summary>
    ReadyToRestart,

    Failed,
}

/// <param name="AvailableVersion">The newer version, from <see cref="UpdatePhase.Available"/> on.</param>
/// <param name="Progress">Download progress 0-100 while <see cref="UpdatePhase.Downloading"/>.</param>
/// <param name="Error">What went wrong, when <see cref="UpdatePhase.Failed"/>.</param>
public sealed record UpdateStatus(
    UpdatePhase Phase,
    string CurrentVersion,
    string? AvailableVersion = null,
    int Progress = 0,
    string? Error = null,
    DateTimeOffset? LastChecked = null)
{
    /// <summary>True while a check or download is running.</summary>
    public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading;

    /// <summary>One line for Settings and the tray tooltip.</summary>
    public string Describe() => Phase switch
    {
        UpdatePhase.NotInstalled => $"Version {CurrentVersion} (development build). Updates work in the installed app.",
        UpdatePhase.Idle => $"Version {CurrentVersion}.",
        UpdatePhase.Checking => $"Version {CurrentVersion}. Checking for updates…",
        UpdatePhase.UpToDate => $"Version {CurrentVersion} is the latest.",
        UpdatePhase.Available => $"Version {AvailableVersion} is available (you have {CurrentVersion}).",
        UpdatePhase.Downloading => $"Downloading version {AvailableVersion}… {Progress}%",
        UpdatePhase.ReadyToRestart => $"Version {AvailableVersion} is ready. Update restarts Clicky into it.",
        UpdatePhase.Failed => $"Version {CurrentVersion}. Couldn't update: {Error}",
        _ => $"Version {CurrentVersion}.",
    };
}
