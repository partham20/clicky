using System.Reflection;
using Clicky.Core.Settings;
using Clicky.Core.Updates;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace Clicky.App.Hosting;

/// <summary>
/// <see cref="IUpdateBackend"/> over Velopack. The feed is GitHub releases of the Clicky repository
/// unless settings.updates.feedUrl names another one (a folder works too, for testing a release).
/// </summary>
internal sealed class VelopackUpdateBackend : IUpdateBackend
{
    public const string DefaultRepository = "https://github.com/partham20/clicky";

    private readonly ISettingsStore _settings;
    private readonly ILogger<VelopackUpdateBackend> _logger;
    private readonly Lock _gate = new();

    private UpdateManager? _manager;
    private (string Feed, bool Prerelease) _managerFeed;
    private UpdateInfo? _found;

    public VelopackUpdateBackend(ISettingsStore settings, ILogger<VelopackUpdateBackend> logger)
    {
        _settings = settings;
        _logger = logger;

        if (VelopackLocator.IsCurrentSet)
        {
            VelopackLocator.Current.AddLogger(new LogBridge(logger));
        }
    }

    public bool IsInstalled => Manager().IsInstalled;

    public string CurrentVersion =>
        Manager().CurrentVersion?.ToString()
        ?? typeof(VelopackUpdateBackend).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public string? PendingVersion => Manager().UpdatePendingRestart?.Version.ToString();

    /// <summary>Velopack's own log (install, update, uninstall), for bug reports. Null in a development build.</summary>
    public static string? InstallerLogPath
    {
        get
        {
            if (!VelopackLocator.IsCurrentSet || VelopackLocator.Current.AppId is not { Length: > 0 } appId)
            {
                return null;
            }

            // Velopack 1.2 logs every app to %LocalAppData%\velopack\velopack_<packId>.log.
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "velopack", $"velopack_{appId}.log");
            return File.Exists(path) ? path : null;
        }
    }

    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        var manager = Manager();
        var found = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _found = found;
        }

        return found?.TargetFullRelease.Version.ToString();
    }

    public async Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
    {
        UpdateInfo found;
        lock (_gate)
        {
            found = _found ?? throw new InvalidOperationException("Check for updates before downloading.");
        }

        await Manager().DownloadUpdatesAsync(found, progress, cancellationToken).ConfigureAwait(false);
    }

    public void ApplyAfterExit()
    {
        var manager = Manager();
        var target = manager.UpdatePendingRestart ?? throw new InvalidOperationException("No downloaded update to apply.");

        // Shows Velopack's small progress window, waits up to 60 s for us to exit, then restarts us.
        manager.WaitExitThenApplyUpdates(target, silent: false, restart: true);
    }

    private UpdateManager Manager()
    {
        var updates = _settings.Current.Updates;
        var feed = string.IsNullOrWhiteSpace(updates.FeedUrl) ? DefaultRepository : updates.FeedUrl;
        lock (_gate)
        {
            if (_manager is null || _managerFeed != (feed, updates.Prerelease))
            {
                _manager = Create(feed, updates.Prerelease);
                _managerFeed = (feed, updates.Prerelease);
                _found = null;
                _logger.LogDebug("Update feed: {Feed}{Prerelease}", feed, updates.Prerelease ? " (with pre-releases)" : string.Empty);
            }

            return _manager;
        }
    }

    private static UpdateManager Create(string feed, bool prerelease) =>
        feed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            ? new UpdateManager(new GithubSource(feed, accessToken: null, prerelease))
            : new UpdateManager(feed);

    /// <summary>Velopack's diagnostics into Clicky's log, one level down (it is chatty).</summary>
    private sealed class LogBridge(ILogger logger) : IVelopackLogger
    {
        public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
        {
            var level = logLevel switch
            {
                VelopackLogLevel.Critical or VelopackLogLevel.Error => LogLevel.Warning,
                VelopackLogLevel.Warning or VelopackLogLevel.Information => LogLevel.Debug,
                _ => LogLevel.Trace,
            };
            logger.Log(level, exception, "Velopack: {Message}", message);
        }
    }
}
