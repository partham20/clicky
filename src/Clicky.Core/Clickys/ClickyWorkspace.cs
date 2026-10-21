using System.Text;

namespace Clicky.Core.Clickys;

/// <summary>A file in a Clicky's folder.</summary>
/// <param name="Name">Path relative to the folder, with forward slashes.</param>
public sealed record ClickyFile(string Name, string FullPath, long Length, DateTimeOffset Modified);

/// <summary>
/// Each Clicky's files folder (%AppData%\Clicky\clickys\{id}): the user drops reference files in,
/// and Home saves replies there. Small text files are sent with every run.
/// </summary>
public sealed class ClickyWorkspace(string root)
{
    /// <summary>Text files up to this size go into the prompt whole.</summary>
    public const int MaxFileChars = 16_000;

    /// <summary>All included file text together stays under this.</summary>
    public const int MaxTotalChars = 48_000;

    /// <summary>Files listed in the prompt (the rest are counted).</summary>
    public const int MaxListedFiles = 50;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".ini", ".toml", ".log",
        ".html", ".htm", ".css", ".js", ".ts", ".cs", ".py", ".c", ".h", ".cpp", ".hpp", ".sql", ".ps1", ".bat", ".sh",
    };

    public string Root { get; } = Path.GetFullPath(root);

    public static ClickyWorkspace For(ClickyPaths paths) => new(Path.Combine(paths.Root, "clickys"));

    public string DirectoryFor(string clickyId) => Path.Combine(Root, SafeId(clickyId));

    /// <summary>Creates the folder if needed and returns its path.</summary>
    public string EnsureCreated(string clickyId)
    {
        var directory = DirectoryFor(clickyId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>The Clicky's files, newest first. Never throws for I/O problems.</summary>
    public IReadOnlyList<ClickyFile> ListFiles(string clickyId)
    {
        var directory = DirectoryFor(clickyId);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            return [.. new DirectoryInfo(directory).EnumerateFiles("*", options)
                .Select(f => new ClickyFile(Path.GetRelativePath(directory, f.FullName).Replace('\\', '/'), f.FullName, f.Length, f.LastWriteTimeUtc))
                .OrderByDescending(f => f.Modified)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The "Your files" part of a run's prompt: every file by name, and the text of small text files
    /// up to the caps. Empty when there are no files.
    /// </summary>
    public string DescribeForPrompt(string clickyId)
    {
        var files = ListFiles(clickyId);
        if (files.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        var budget = MaxTotalChars;
        foreach (var file in files.Take(MaxListedFiles))
        {
            text.Append("- ").Append(file.Name).Append(" (").Append(Size(file.Length)).Append(')');
            if (TextExtensions.Contains(Path.GetExtension(file.Name)) && file.Length <= MaxFileChars * 4L && budget > 0)
            {
                var content = TryRead(file.FullPath);
                if (content is not null && content.Length <= MaxFileChars && content.Length <= budget)
                {
                    budget -= content.Length;
                    text.Append(":\n<file name=\"").Append(file.Name.Replace("\"", "'", StringComparison.Ordinal)).Append("\">\n")
                        .Append(content.TrimEnd()).Append("\n</file>");
                }
                else if (content is not null)
                {
                    text.Append(", too long to include");
                }
            }

            text.Append('\n');
        }

        if (files.Count > MaxListedFiles)
        {
            text.Append("- …and ").Append(files.Count - MaxListedFiles).Append(" more files\n");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Writes <paramref name="content"/> to a new file, adding " (2)", " (3)"… rather than overwriting.</summary>
    /// <returns>The full path written.</returns>
    public async Task<string> SaveAsync(string clickyId, string fileName, string content, CancellationToken cancellationToken = default)
    {
        var directory = EnsureCreated(clickyId);
        var safe = SafeFileName(fileName);
        var stem = Path.GetFileNameWithoutExtension(safe);
        var extension = Path.GetExtension(safe);
        var path = Path.Combine(directory, safe);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(directory, $"{stem} ({n}){extension}");
        }

        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return path;
    }

    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var cleaned = new string([.. (name ?? string.Empty).Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c)]).Trim().Trim('.');
        if (cleaned.Length == 0)
        {
            cleaned = "reply.md";
        }

        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    private static string SafeId(string clickyId)
    {
        // Ids are hex; anything else (a stale or hand-made id) must not walk out of the root.
        if (string.IsNullOrEmpty(clickyId) || clickyId.AsSpan().ContainsAnyExcept(ClickyDraft.HexDigits))
        {
            throw new ArgumentException($"Not a Clicky id: '{clickyId}'.", nameof(clickyId));
        }

        return clickyId;
    }

    private static string? TryRead(string path)
    {
        try
        {
            var content = File.ReadAllText(path);
            return content.Contains('\0', StringComparison.Ordinal) ? null : content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}
