using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Dictation;

/// <summary>
/// The local backup of every dictation: plain text, one file per day (dictation\yyyy-MM-dd.txt),
/// written chunk by chunk as it is transcribed, so a crash, a dead mic or a failed insert never
/// loses what was said. Audio is never kept. Files older than settings.dictation.keepHistoryDays
/// are deleted.
/// </summary>
/// <remarks>Thread-safe. Write failures are logged and otherwise ignored: the backup must never break dictation.</remarks>
public sealed class DictationHistory(ClickyPaths paths, TimeProvider time, ILogger<DictationHistory> logger)
{
    private const string FilePattern = "????-??-??.txt";

    private readonly Lock _gate = new();

    public string Directory => paths.DictationHistoryDirectory;

    /// <summary>Starts a session's block: a blank line and a time stamp heading.</summary>
    public void BeginSession(string mode, int keepDays)
    {
        if (keepDays <= 0)
        {
            return;
        }

        var now = time.GetLocalNow();
        Append(now, $"{Environment.NewLine}[{now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {mode}]{Environment.NewLine}");
    }

    /// <summary>Adds one transcribed chunk to the current session.</summary>
    public void Add(string text, int keepDays)
    {
        if (keepDays <= 0 || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Append(time.GetLocalNow(), text.Trim() + Environment.NewLine);
    }

    /// <summary>Deletes day files older than <paramref name="keepDays"/> (all of them for 0).</summary>
    public void Prune(int keepDays)
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return;
            }

            var cutoff = DateOnly.FromDateTime(time.GetLocalNow().Date).AddDays(-keepDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, FilePattern))
            {
                if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    && (keepDays <= 0 || day < cutoff))
                {
                    File.Delete(file);
                    logger.LogDebug("Dictation history: deleted {File}", file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Dictation history: could not prune {Directory}", Directory);
        }
    }

    private void Append(DateTimeOffset now, string text)
    {
        var path = Path.Combine(Directory, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".txt");
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(path, text, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Dictation history: could not write {Path}", path);
            }
        }
    }
}
