using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Settings;

public sealed record SettingsStoreOptions
{
    /// <summary>
    /// Editors fire several change notifications per save (truncate, write, attribute touch), and
    /// rename-based saves fire Deleted/Created/Renamed. Coalesce them into one reload.
    /// </summary>
    public TimeSpan ReloadDebounce { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// settings.json backed store with atomic writes and live reload.
/// <list type="bullet">
/// <item>Writes go to a sibling .tmp file and are swapped in with File.Replace, so a crash or power
/// loss mid-write leaves either the old or the new file, never a truncated one.</item>
/// <item>A file that is corrupt at startup is moved aside as settings.json.&lt;timestamp&gt;.bad and
/// defaults are written. A file that becomes invalid while running (someone is mid-edit) is left
/// untouched and the last good settings stay active.</item>
/// <item>Our own writes are recognised by content and do not echo back as file changes.</item>
/// </list>
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    private const int IoRetryCount = 5;

    private readonly string _path;
    private readonly string _directory;
    private readonly string _fileName;
    private readonly ILogger _logger;
    private readonly SettingsStoreOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ITimer _reloadTimer;

    private ClickySettings _current = ClickySettings.Default;

    // Exact bytes we last read or wrote. A watcher event whose file content matches is our own echo
    // (or a touch without a content change) and is ignored.
    private byte[] _lastKnownBytes = [];

    private FileSystemWatcher? _watcher;
    private volatile bool _disposed;

    private JsonSettingsStore(string path, ILogger logger, SettingsStoreOptions options)
    {
        _path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_path) ?? throw new ArgumentException("Settings path has no directory.", nameof(path));
        _fileName = Path.GetFileName(_path);
        _logger = logger;
        _options = options;
        _reloadTimer = options.TimeProvider.CreateTimer(OnReloadTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public ClickySettings Current => Volatile.Read(ref _current);

    public string FilePath => _path;

    /// <summary>
    /// Loads (or creates) the settings file synchronously and starts watching it. Intended to run
    /// once at startup before the UI exists, so blocking I/O here is deliberate.
    /// </summary>
    public static JsonSettingsStore Open(string path, ILogger<JsonSettingsStore> logger, SettingsStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        var store = new JsonSettingsStore(path, logger, options ?? new SettingsStoreOptions());
        try
        {
            store.LoadAtStartup();
            store.StartWatching();
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public async Task UpdateAsync(Func<ClickySettings, ClickySettings> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ClickySettings previous;
        ClickySettings next;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            previous = _current;
            next = (mutate(previous) ?? throw new InvalidOperationException("Settings mutation returned null.")).Normalize();
            if (next == previous)
            {
                return;
            }

            await PersistAsync(next, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, next);
        }
        finally
        {
            _gate.Release();
        }

        // Outside the gate so a handler may call UpdateAsync without deadlocking.
        RaiseChanged(previous, next, SettingsChangeSource.App);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _reloadTimer.Dispose();
        // _gate is intentionally not disposed: a reload may still be awaiting it, and SemaphoreSlim
        // holds no unmanaged resources unless AvailableWaitHandle is touched.
    }

    private void LoadAtStartup()
    {
        Directory.CreateDirectory(_directory);

        if (!File.Exists(_path))
        {
            _logger.LogInformation("No settings file at {Path}; writing defaults", _path);
            PersistSync(ClickySettings.Default);
            _current = ClickySettings.Default;
            return;
        }

        var bytes = ReadAllBytesWithRetry(_path);
        if (TryDeserialize(bytes, out var loaded, out var error))
        {
            if (loaded.SchemaVersion > ClickySettings.CurrentSchemaVersion)
            {
                // Written by a newer build (downgrade). Unknown properties are dropped on our next write.
                _logger.LogWarning(
                    "Settings schema {FileVersion} is newer than supported {SupportedVersion}; unknown settings will be lost on next save",
                    loaded.SchemaVersion,
                    ClickySettings.CurrentSchemaVersion);
            }

            _current = loaded;
            _lastKnownBytes = bytes;
            _logger.LogInformation("Loaded settings from {Path}", _path);
            return;
        }

        var quarantine = $"{_path}.{_options.TimeProvider.GetLocalNow():yyyyMMdd-HHmmss}.bad";
        _logger.LogWarning("Settings file {Path} is unreadable ({Error}); moved to {Quarantine} and restored defaults", _path, error, quarantine);
        File.Move(_path, quarantine, overwrite: true);
        PersistSync(ClickySettings.Default);
        _current = ClickySettings.Default;
    }

    private void StartWatching()
    {
        _watcher?.Dispose();

        // Filter is "*" and the name is checked in the handler: rename-based saves (write temp, rename
        // over target) do not reliably match a name filter on both the old and new name.
        var watcher = new FileSystemWatcher(_directory)
        {
            Filter = "*",
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        watcher.Changed += OnFileEvent;
        watcher.Created += OnFileEvent;
        watcher.Renamed += OnFileEvent;
        watcher.Error += OnWatcherError;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (_disposed || !string.Equals(e.Name, _fileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _reloadTimer.Change(_options.ReloadDebounce, Timeout.InfiniteTimeSpan);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // Typically an internal buffer overflow; events were lost, so rebuild and re-read.
        _logger.LogWarning(e.GetException(), "Settings watcher failed; restarting it");
        try
        {
            StartWatching();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogError(ex, "Could not restart settings watcher; live reload is disabled until restart");
        }

        _reloadTimer.Change(_options.ReloadDebounce, Timeout.InfiniteTimeSpan);
    }

    private void OnReloadTimer(object? state) => _ = ReloadFromDiskAsync();

    private async Task ReloadFromDiskAsync()
    {
        ClickySettings previous;
        ClickySettings next;

        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            byte[] bytes;
            try
            {
                bytes = ReadAllBytesWithRetry(_path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                _logger.LogWarning("Settings file {Path} was removed; keeping current settings", _path);
                return;
            }

            if (bytes.AsSpan().SequenceEqual(_lastKnownBytes))
            {
                return;
            }

            if (!TryDeserialize(bytes, out var loaded, out var error))
            {
                _logger.LogWarning("Ignoring invalid edit to {Path} ({Error}); previous settings stay active", _path, error);
                return;
            }

            _lastKnownBytes = bytes;
            previous = _current;
            if (loaded == previous)
            {
                return;
            }

            next = loaded;
            Volatile.Write(ref _current, next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not reload {Path}; previous settings stay active", _path);
            return;
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation("Reloaded settings from {Path}", _path);
        RaiseChanged(previous, next, SettingsChangeSource.File);
    }

    private void RaiseChanged(ClickySettings previous, ClickySettings current, SettingsChangeSource source)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        var args = new SettingsChangedEventArgs(previous, current, source);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<SettingsChangedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                // One faulty subscriber must not stop the others or kill the watcher thread.
                _logger.LogError(ex, "Settings change handler {Handler} threw", handler.Method.Name);
            }
        }
    }

    private static bool TryDeserialize(byte[] bytes, out ClickySettings settings, out string? error)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize(bytes, SettingsJsonContext.Default.ClickySettings);
            if (parsed is null)
            {
                settings = ClickySettings.Default;
                error = "file contains null";
                return false;
            }

            settings = parsed.Normalize();
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            settings = ClickySettings.Default;
            error = ex.Message;
            return false;
        }
    }

    private static byte[] Serialize(ClickySettings settings) =>
        JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.ReadableClickySettings);

    private void PersistSync(ClickySettings settings) =>
        PersistAsync(settings, CancellationToken.None).GetAwaiter().GetResult();

    private async Task PersistAsync(ClickySettings settings, CancellationToken cancellationToken)
    {
        var bytes = Serialize(settings);
        var tempPath = _path + ".tmp";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.WriteThrough);
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                // Record before the swap so the watcher event it triggers is recognised as ours.
                _lastKnownBytes = bytes;

                if (File.Exists(_path))
                {
                    File.Replace(tempPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, _path);
                }

                return;
            }
            catch (IOException ex) when (attempt < IoRetryCount && ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                // Sharing violations from editors, AV scanners and OneDrive are transient.
                _logger.LogDebug(ex, "Settings write attempt {Attempt} failed; retrying", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static byte[] ReadAllBytesWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // FileShare.ReadWrite|Delete so we never block an editor that is saving.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[stream.Length];
                stream.ReadExactly(buffer);
                return buffer;
            }
            catch (IOException) when (attempt < IoRetryCount && File.Exists(path))
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
