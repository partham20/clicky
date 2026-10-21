using System.Net.Http;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Updates;

/// <summary>
/// Finds, downloads and applies new versions of the installed app. Checks a minute after startup
/// and every six hours (settings.updates.autoCheck), downloads in the background
/// (settings.updates.autoDownload), and leaves the restart to the user: <see cref="RestartToUpdate"/>
/// behind the Update button. Never restarts on its own; an update left waiting is applied at the
/// next start by the installer anyway.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly IUpdateBackend _backend;
    private readonly ISettingsStore _settings;
    private readonly ILogger<UpdateService> _logger;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();

    private UpdateStatus _status;
    private Task? _running;
    private ITimer? _timer;

    public UpdateService(IUpdateBackend backend, ISettingsStore settings, ILogger<UpdateService> logger, TimeProvider? time = null)
    {
        _backend = backend;
        _settings = settings;
        _logger = logger;
        _time = time ?? TimeProvider.System;

        var current = backend.CurrentVersion;
        _status = !backend.IsInstalled ? new UpdateStatus(UpdatePhase.NotInstalled, current)
            : backend.PendingVersion is { } pending ? new UpdateStatus(UpdatePhase.ReadyToRestart, current, pending)
            : new UpdateStatus(UpdatePhase.Idle, current);
    }

    /// <summary>Raised on a background thread whenever <see cref="Status"/> changes.</summary>
    public event EventHandler<UpdateStatus>? StatusChanged;

    public UpdateStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Starts the background schedule. Does nothing in a development build.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (!_backend.IsInstalled || _timer is not null)
            {
                return;
            }

            _timer = _time.CreateTimer(_ => OnSchedule(), null, FirstCheckDelay, Timeout.InfiniteTimeSpan);
        }

        _logger.LogInformation("Clicky {Version} installed; checking for updates every {Hours} h", _backend.CurrentVersion, CheckInterval.TotalHours);
    }

    /// <summary>
    /// Checks now and downloads whatever it finds, whatever the settings say (the user asked).
    /// Joins a check that is already running instead of starting a second one.
    /// </summary>
    public Task CheckNowAsync() => Run(download: true, reason: "requested");

    /// <summary>
    /// Hands the downloaded version to the updater. On true the caller must exit the app: the
    /// updater waits for that, installs, and starts the new version.
    /// </summary>
    public bool RestartToUpdate()
    {
        var status = Status;
        if (status.Phase != UpdatePhase.ReadyToRestart)
        {
            return false;
        }

        try
        {
            _backend.ApplyAfterExit();
            _logger.LogInformation("Restarting to update from {Current} to {Version}", status.CurrentVersion, status.AvailableVersion);
            return true;
        }
        catch (Exception ex) when (IsUpdateFailure(ex))
        {
            _logger.LogError(ex, "Could not start the updater");
            Set(s => s with { Phase = UpdatePhase.Failed, Error = Describe(ex) });
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        _stopping.Cancel();
        _stopping.Dispose();
    }

    private void OnSchedule()
    {
        // One-shot, re-armed each time, so a slow check can never overlap the next tick.
        lock (_gate)
        {
            _timer?.Change(CheckInterval, Timeout.InfiniteTimeSpan);
        }

        var settings = _settings.Current.Updates;
        if (settings.AutoCheck)
        {
            _ = Run(settings.AutoDownload, "scheduled");
        }
    }

    private Task Run(bool download, string reason)
    {
        lock (_gate)
        {
            // A downloaded update stays put until the restart; checking again would only re-download.
            if (!_backend.IsInstalled || _status.Phase == UpdatePhase.ReadyToRestart || _stopping.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            if (_running is { IsCompleted: false } running)
            {
                return running;
            }

            _logger.LogDebug("Update check ({Reason})", reason);
            var token = _stopping.Token;
            return _running = Task.Run(() => CheckAndDownloadAsync(download, token), CancellationToken.None);
        }
    }

    private async Task CheckAndDownloadAsync(bool download, CancellationToken cancellationToken)
    {
        Set(s => s with { Phase = UpdatePhase.Checking, Progress = 0, Error = null });

        string? version;
        try
        {
            version = await _backend.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUpdateFailure(ex))
        {
            Fail("check for updates", ex);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var checkedAt = _time.GetUtcNow();
        if (version is null)
        {
            Set(s => s with { Phase = UpdatePhase.UpToDate, AvailableVersion = null, LastChecked = checkedAt });
            _logger.LogDebug("Clicky {Version} is up to date", _backend.CurrentVersion);
            return;
        }

        _logger.LogInformation("Clicky {Version} is available (running {Current})", version, _backend.CurrentVersion);
        if (!download)
        {
            Set(s => s with { Phase = UpdatePhase.Available, AvailableVersion = version, LastChecked = checkedAt });
            return;
        }

        Set(s => s with { Phase = UpdatePhase.Downloading, AvailableVersion = version, Progress = 0, LastChecked = checkedAt });
        try
        {
            await _backend.DownloadAsync(OnProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUpdateFailure(ex))
        {
            Fail($"download version {version}", ex);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Set(s => s with { Phase = UpdatePhase.ReadyToRestart, Progress = 100 });
        _logger.LogInformation("Clicky {Version} downloaded; waiting for a restart", version);
    }

    private void OnProgress(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        Set(s => s.Phase == UpdatePhase.Downloading ? s with { Progress = percent } : s);
    }

    private void Fail(string what, Exception ex)
    {
        // Offline is normal for a laptop; the next scheduled check tries again.
        _logger.LogWarning(ex, "Could not {What}", what);
        Set(s => s with { Phase = UpdatePhase.Failed, Error = Describe(ex) });
    }

    private void Set(Func<UpdateStatus, UpdateStatus> change)
    {
        UpdateStatus next;
        lock (_gate)
        {
            next = change(_status);
            if (next == _status)
            {
                return;
            }

            _status = next;
        }

        StatusChanged?.Invoke(this, next);
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException => "couldn't reach the update server. Check the internet connection.",
        TimeoutException => "the update server took too long to answer.",
        _ => ex.Message,
    };

    // The backend wraps a third-party updater whose failures are not one documented type, so every
    // exception except cancellation counts as "this check failed, try again later".
    private static bool IsUpdateFailure(Exception ex) => ex is not OperationCanceledException;
}
