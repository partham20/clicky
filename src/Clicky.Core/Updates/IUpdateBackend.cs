namespace Clicky.Core.Updates;

/// <summary>
/// The installer's update machinery (Velopack in the app). Kept behind an interface so the
/// schedule and states in <see cref="UpdateService"/> are tested without installing anything.
/// </summary>
public interface IUpdateBackend
{
    /// <summary>False for a development build run from bin\: there is nothing to update.</summary>
    bool IsInstalled { get; }

    /// <summary>Installed version, or the assembly version for a development build.</summary>
    string CurrentVersion { get; }

    /// <summary>A version downloaded earlier and waiting for a restart, if any.</summary>
    string? PendingVersion { get; }

    /// <summary>Asks the feed for a newer version. Null when this one is the latest.</summary>
    Task<string?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the version the last <see cref="CheckAsync"/> found. Progress is 0-100.</summary>
    Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the updater, which waits for this process to exit, installs the downloaded version and
    /// starts it. The caller then exits normally.
    /// </summary>
    void ApplyAfterExit();
}
