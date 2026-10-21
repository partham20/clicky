using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Clicky.Core;
using Clicky.Core.Agent.Browser;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Clicky.Platform.Windows.Agent;

/// <summary>
/// Clicky's own browser: Chrome (or Edge when Chrome isn't installed) on a dedicated profile under
/// %LocalAppData%\Clicky\browser, with remote debugging on a port Chrome picks. The user can sign in
/// there; their everyday profile is never touched.
/// </summary>
/// <remarks>
/// Gotchas: Chrome 136+ ignores --remote-debugging-port on the default profile, which is one more
/// reason for the separate one. With port 0 Chrome writes the port it chose to DevToolsActivePort in
/// the profile, and a second launch with the same profile hands over to the running browser and
/// exits, so an existing file is tried first. The browser's pid comes from CDP, not from the process
/// we start, for the same reason.
/// </remarks>
public sealed class ChromeHost : IAsyncDisposable
{
    private static readonly TimeSpan s_startTimeout = TimeSpan.FromSeconds(15);

    private readonly ClickyPaths _paths;
    private readonly ISettingsStore _settings;
    private readonly ILogger<ChromeHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CdpConnection? _connection;
    private CdpBrowser? _browser;
    private uint _browserPid;

    public ChromeHost(ClickyPaths paths, ISettingsStore settings, ILogger<ChromeHost> logger)
    {
        _paths = paths;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>The browser if Clicky already has it open; never launches it.</summary>
    public CdpBrowser? Connected => _browser is { IsConnected: true } browser ? browser : null;

    /// <summary>The browser process (owner of its top-level windows), or 0 when not connected.</summary>
    public uint BrowserProcessId => Connected is null ? 0 : _browserPid;

    /// <summary>Connects to Clicky's browser, starting it if needed.</summary>
    /// <returns>Null if no Chromium browser is installed or it didn't start in time.</returns>
    public async Task<CdpBrowser?> EnsureAsync(CancellationToken cancellationToken)
    {
        if (Connected is { } ready)
        {
            return ready;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Connected is { } raced)
            {
                return raced;
            }

            await DropAsync().ConfigureAwait(false);
            var profile = _paths.BrowserProfileDirectory;
            Directory.CreateDirectory(profile);
            var portFile = Path.Combine(profile, "DevToolsActivePort");

            if (await TryConnectAsync(portFile, cancellationToken).ConfigureAwait(false))
            {
                return _browser;
            }

            var executable = FindBrowser(_settings.Current.Agent.BrowserPath);
            if (executable is null)
            {
                _logger.LogWarning("No Chrome or Edge found for Clicky's browser");
                return null;
            }

            TryDelete(portFile);
            Launch(executable, profile);
            var deadline = DateTime.UtcNow + s_startTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                if (await TryConnectAsync(portFile, cancellationToken).ConfigureAwait(false))
                {
                    return _browser;
                }
            }

            _logger.LogWarning("Clicky's browser ({Browser}) did not open its debugging port within {Timeout}", Path.GetFileName(executable), s_startTimeout);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The browser keeps running: the user may be signed in and using it.
        await DropAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>The configured browser if it exists, else Chrome, else Edge.</summary>
    internal static string? FindBrowser(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        foreach (var exe in new[] { "chrome.exe", "msedge.exe" })
        {
            if (AppPath(exe) is { } registered)
            {
                return registered;
            }

            var folder = exe == "chrome.exe" ? Path.Combine("Google", "Chrome", "Application") : Path.Combine("Microsoft", "Edge", "Application");
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            })
            {
                var candidate = Path.Combine(root, folder, exe);
                if (!string.IsNullOrEmpty(root) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? AppPath(string exe)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"')))
            {
                return path.Trim('"');
            }
        }

        return null;
    }

    private void Launch(string executable, string profile)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in new[]
        {
            $"--user-data-dir={profile}",
            "--remote-debugging-port=0",
            "--no-first-run",
            "--no-default-browser-check",
            "about:blank",
        })
        {
            start.ArgumentList.Add(argument);
        }

        // Lets the new window come to the front: a tray app has no foreground rights of its own.
        ForegroundRights.GrantToAnyProcess();
        Process.Start(start)?.Dispose();
        _logger.LogInformation("Started Clicky's browser: {Browser}", Path.GetFileName(executable));
    }

    private async Task<bool> TryConnectAsync(string portFile, CancellationToken cancellationToken)
    {
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(portFile, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (lines.Length < 2 || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is <= 0 or > 65535)
        {
            return false;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var connection = await CdpConnection.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{lines[1].Trim()}"), timeout.Token).ConfigureAwait(false);
            var info = await connection.SendAsync("SystemInfo.getProcessInfo", cancellationToken: timeout.Token).ConfigureAwait(false);
            _browserPid = BrowserPid(info);
            _connection = connection;
            _browser = new CdpBrowser(connection);
            _logger.LogInformation("Connected to Clicky's browser on port {Port} (pid {Pid})", port, _browserPid);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A stale file from a browser that has since closed.
            _logger.LogDebug(ex, "No browser behind DevToolsActivePort {Port}", port);
            return false;
        }
    }

    private static uint BrowserPid(JsonElement info)
    {
        if (info.TryGetProperty("processInfo", out var processes) && processes.ValueKind == JsonValueKind.Array)
        {
            foreach (var process in processes.EnumerateArray())
            {
                if (process.TryGetProperty("type", out var type) && type.GetString() == "browser"
                    && process.TryGetProperty("id", out var id) && id.TryGetUInt32(out var pid))
                {
                    return pid;
                }
            }
        }

        return 0;
    }

    private async Task DropAsync()
    {
        _browser = null;
        _browserPid = 0;
        if (_connection is { } connection)
        {
            _connection = null;
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
