using System.Text;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Memory;

/// <summary>Both files as text and as parsed documents, plus when each was last written.</summary>
public sealed record MemorySnapshot(MemorySet Memory, string ProfileText, string VolatileText, DateTimeOffset? ProfileWritten, DateTimeOffset? VolatileWritten)
{
    public string Text(MemoryFile file) => file == MemoryFile.Profile ? ProfileText : VolatileText;

    public DateTimeOffset? Written(MemoryFile file) => file == MemoryFile.Profile ? ProfileWritten : VolatileWritten;
}

public enum MemoryChangeSource
{
    /// <summary>Clicky's updater pass after a conversation.</summary>
    Updater,

    /// <summary>Saved from Settings › Memory.</summary>
    User,

    /// <summary>Forget, or undo of an updater pass.</summary>
    Reset,
}

public sealed class MemoryChangedEventArgs(IReadOnlyList<MemoryFile> files, MemoryChangeSource source) : EventArgs
{
    public IReadOnlyList<MemoryFile> Files { get; } = files;

    public MemoryChangeSource Source { get; } = source;
}

/// <summary>
/// PROFILE.md and VOLATILE.md in %AppData%\Clicky\memory. Plain Markdown so people can open them in any
/// editor; edits made that way are picked up on the next read (no watcher: the files are read once per
/// request and cached by timestamp and size). Writes go to a .tmp sibling and are moved over the file,
/// so a crash leaves the old or the new file, never half of one. Missing files are created from
/// <see cref="MemoryTemplates"/>.
/// </summary>
public sealed class MemoryStore : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger<MemoryStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Cached?[] _cache = new Cached?[2];

    // Text before and after the last updater pass, per file, for "Undo Clicky's last change". Kept for
    // this session only, and refused once the file has changed again.
    private Undo[]? _undo;

    public MemoryStore(string directory, ILogger<MemoryStore> logger, MemoryLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        _logger = logger;
        Limits = limits ?? MemoryLimits.Default;
    }

    /// <summary>Raised on the writing thread after a file changes through this store.</summary>
    public event EventHandler<MemoryChangedEventArgs>? Changed;

    public string Directory { get; }

    public MemoryLimits Limits { get; }

    public bool CanUndo => Volatile.Read(ref _undo) is not null;

    public string PathOf(MemoryFile file) => Path.Combine(Directory, MemoryTemplates.FileName(file));

    public async Task<MemorySnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Saves a file as typed in Settings. Not capped: people may write as much as they like.</summary>
    public async Task SaveTextAsync(MemoryFile file, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteLockedAsync(file, text.ReplaceLineEndings("\n"), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Raise([file], MemoryChangeSource.User);
    }

    /// <summary>Resets one file (or both) to its empty template and drops the undo copy, so nothing comes back.</summary>
    public async Task ForgetAsync(MemoryFile? file = null, CancellationToken cancellationToken = default)
    {
        MemoryFile[] files = file is { } one ? [one] : [MemoryFile.Profile, MemoryFile.Volatile];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var f in files)
            {
                await WriteLockedAsync(f, MemoryTemplates.Empty(f).ToMarkdown(), cancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(ref _undo, null);
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation("Memory forgotten: {Files}", string.Join(", ", files));
        Raise(files, MemoryChangeSource.Reset);
    }

    /// <summary>
    /// Merges the updater's edits into the files as they are on disk right now (so a hand edit made
    /// while the model was thinking is kept) and writes the files that changed.
    /// </summary>
    public async Task<MergeResult> ApplyAsync(IReadOnlyList<MemoryEdit> edits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        MergeResult result;
        MemoryFile[] changed;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
            result = MemoryMerger.Apply(before.Memory, edits, Limits);
            changed = [.. Enum.GetValues<MemoryFile>().Where(result.Changed)];
            if (changed.Length == 0)
            {
                return result;
            }

            var undo = new List<Undo>();
            foreach (var file in changed)
            {
                var text = result.Memory[file].ToMarkdown();
                await WriteLockedAsync(file, text, CancellationToken.None).ConfigureAwait(false);
                undo.Add(new Undo(file, before.Text(file), text));
            }

            Volatile.Write(ref _undo, [.. undo]);
        }
        finally
        {
            _gate.Release();
        }

        Raise(changed, MemoryChangeSource.Updater);
        return result;
    }

    /// <summary>Puts back the files as they were before the last updater pass.</summary>
    /// <returns>False when there is nothing to undo or a file has changed since (that edit is kept).</returns>
    public async Task<bool> UndoLastUpdateAsync(CancellationToken cancellationToken = default)
    {
        MemoryFile[] files;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var undo = Volatile.Read(ref _undo);
            if (undo is null)
            {
                return false;
            }

            var current = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
            if (undo.Any(u => current.Text(u.File) != u.After))
            {
                Volatile.Write(ref _undo, null);
                return false;
            }

            foreach (var u in undo)
            {
                await WriteLockedAsync(u.File, u.Before, CancellationToken.None).ConfigureAwait(false);
            }

            files = [.. undo.Select(u => u.File)];
            Volatile.Write(ref _undo, null);
        }
        finally
        {
            _gate.Release();
        }

        Raise(files, MemoryChangeSource.Reset);
        return true;
    }

    private async Task<MemorySnapshot> ReadLockedAsync(CancellationToken cancellationToken)
    {
        var (profile, profileWritten) = await ReadFileLockedAsync(MemoryFile.Profile, cancellationToken).ConfigureAwait(false);
        var (volatileText, volatileWritten) = await ReadFileLockedAsync(MemoryFile.Volatile, cancellationToken).ConfigureAwait(false);
        return new MemorySnapshot(
            new MemorySet(MemoryDocument.Parse(profile), MemoryDocument.Parse(volatileText)),
            profile,
            volatileText,
            profileWritten,
            volatileText.Length == 0 ? null : volatileWritten);
    }

    private async Task<(string Text, DateTimeOffset Written)> ReadFileLockedAsync(MemoryFile file, CancellationToken cancellationToken)
    {
        var path = PathOf(file);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            await WriteLockedAsync(file, MemoryTemplates.Empty(file).ToMarkdown(), cancellationToken).ConfigureAwait(false);
            info.Refresh();
        }

        var cached = _cache[(int)file];
        if (cached is not null && cached.Written == info.LastWriteTimeUtc && cached.Length == info.Length)
        {
            return (cached.Text, cached.Written);
        }

        string text;
        try
        {
            text = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).ReplaceLineEndings("\n");
        }
        catch (IOException ex)
        {
            // Open in an editor that holds it exclusively: use what we had rather than forgetting everything.
            _logger.LogWarning(ex, "Could not read {Path}", path);
            return (cached?.Text ?? string.Empty, cached?.Written ?? info.LastWriteTimeUtc);
        }

        _cache[(int)file] = new Cached(text, info.LastWriteTimeUtc, info.Length);
        return (text, info.LastWriteTimeUtc);
    }

    private async Task WriteLockedAsync(MemoryFile file, string text, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathOf(file);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, text, Utf8NoBom, cancellationToken).ConfigureAwait(false);

        // Retries cover an editor or indexer briefly holding the file (sharing violation).
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                break;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(50 * attempt, CancellationToken.None).ConfigureAwait(false);
            }
        }

        var info = new FileInfo(path);
        _cache[(int)file] = new Cached(text, info.LastWriteTimeUtc, info.Length);
    }

    public void Dispose() => _gate.Dispose();

    private void Raise(IReadOnlyList<MemoryFile> files, MemoryChangeSource source)
    {
        try
        {
            Changed?.Invoke(this, new MemoryChangedEventArgs(files, source));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "A memory change handler failed");
        }
    }

    private sealed record Cached(string Text, DateTime Written, long Length);

    private sealed record Undo(MemoryFile File, string Before, string After);
}
