using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Diagnostics;

/// <param name="Description">What the person says happened, as typed.</param>
/// <param name="IncludeLogs">Clicky's logs from the last days (they never hold API keys or audio).</param>
/// <param name="IncludeSettings">settings.json, which holds preferences only; keys live in Credential Manager.</param>
public sealed record BugReportRequest(string Description, bool IncludeLogs = true, bool IncludeSettings = true)
{
    /// <summary>Version, Windows build, monitors and similar, as label/value pairs.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> SystemInfo { get; init; } = [];

    /// <summary>More text files to add under installer\ (Velopack's own log).</summary>
    public IReadOnlyList<string> ExtraFiles { get; init; } = [];
}

/// <param name="ZipPath">The report, in %AppData%\Clicky\bug-reports.</param>
/// <param name="Summary">Markdown for the body of a GitHub issue: the description and system info, no logs.</param>
public sealed record BugReport(string ZipPath, string Summary, int LogFiles);

/// <summary>
/// Bundles a bug report into one zip the person can look through and attach to an issue:
/// report.md (their description and the system info), the logs and settings.json, all passed
/// through <see cref="SecretRedactor"/>. Memory, screenshots and recordings are never included.
/// </summary>
public sealed class BugReportWriter(ClickyPaths paths, ILogger<BugReportWriter> logger, TimeProvider? time = null)
{
    /// <summary>Uncompressed log text per report; the newest logs win and an oversized one keeps its end.</summary>
    public const long MaxLogBytes = 30L * 1024 * 1024;

    /// <summary>Older reports beyond this many are deleted when a new one is written.</summary>
    public const int KeepReports = 5;

    private const string ReportPrefix = "clicky-bug-";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<BugReport> WriteAsync(BugReportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(paths.BugReportsDirectory);

        var now = _time.GetLocalNow();
        var name = ReportPrefix + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var zipPath = UniquePath(Path.Combine(paths.BugReportsDirectory, name + ".zip"));
        var partial = zipPath + ".partial";
        var summary = BuildSummary(request, now);
        var logFiles = 0;

        try
        {
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                await AddTextAsync(zip, "report.md", summary, cancellationToken).ConfigureAwait(false);

                if (request.IncludeLogs)
                {
                    logFiles = await AddLogsAsync(zip, cancellationToken).ConfigureAwait(false);
                }

                if (request.IncludeSettings && File.Exists(paths.SettingsFile))
                {
                    await AddFileAsync(zip, paths.SettingsFile, "settings.json", long.MaxValue, cancellationToken).ConfigureAwait(false);
                }

                foreach (var extra in request.ExtraFiles.Where(File.Exists))
                {
                    await AddFileAsync(zip, extra, "installer/" + Path.GetFileName(extra), 5L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(partial, zipPath);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        logger.LogInformation("Bug report written to {Path} ({Logs} log files)", zipPath, logFiles);
        PruneOldReports(zipPath);
        return new BugReport(zipPath, summary, logFiles);
    }

    internal static string BuildSummary(BugReportRequest request, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.AppendLine("## What happened").AppendLine();
        text.AppendLine(string.IsNullOrWhiteSpace(request.Description) ? "(no description)" : request.Description.Trim()).AppendLine();
        text.AppendLine("## System").AppendLine();
        foreach (var (label, value) in request.SystemInfo)
        {
            text.Append("- ").Append(label).Append(": ").AppendLine(value);
        }

        text.Append("- Report created: ").AppendLine(now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        return SecretRedactor.Redact(text.ToString());
    }

    private async Task<int> AddLogsAsync(ZipArchive zip, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.LogsDirectory))
        {
            return 0;
        }

        var budget = MaxLogBytes;
        var added = 0;
        var newestFirst = new DirectoryInfo(paths.LogsDirectory).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc);
        foreach (var file in newestFirst)
        {
            if (budget <= 0)
            {
                break;
            }

            budget -= await AddFileAsync(zip, file.FullName, "logs/" + file.Name, budget, cancellationToken).ConfigureAwait(false);
            added++;
        }

        return added;
    }

    /// <summary>Adds a redacted copy, keeping only the last <paramref name="maxBytes"/>. Returns the bytes read.</summary>
    private async Task<long> AddFileAsync(ZipArchive zip, string path, string entryName, long maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            // The live log is held open by Serilog, so share both ways.
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var skipped = 0L;
            if (source.Length > maxBytes)
            {
                skipped = source.Length - maxBytes;
                source.Seek(skipped, SeekOrigin.Begin);
            }

            using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (skipped > 0)
            {
                // Drop the partial first line after seeking into the middle of the file.
                var firstBreak = text.IndexOf('\n', StringComparison.Ordinal);
                text = $"[... first {skipped:N0} bytes left out ...]\n" + (firstBreak >= 0 ? text[(firstBreak + 1)..] : text);
            }

            await AddTextAsync(zip, entryName, SecretRedactor.Redact(text), cancellationToken).ConfigureAwait(false);
            return source.Length - skipped;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Bug report: could not read {Path}", path);
            await AddTextAsync(zip, entryName + ".unreadable.txt", $"Could not read this file: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private static async Task AddTextAsync(ZipArchive zip, string entryName, string text, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var writer = new StreamWriter(await entry.OpenAsync(cancellationToken).ConfigureAwait(false), new UTF8Encoding(false));
        await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private void PruneOldReports(string keep)
    {
        var old = new DirectoryInfo(paths.BugReportsDirectory)
            .GetFiles(ReportPrefix + "*.zip")
            .OrderByDescending(f => f.CreationTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Where(f => !string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase))
            .Skip(KeepReports - 1);
        foreach (var file in old)
        {
            TryDelete(file.FullName);
        }
    }

    private static string UniquePath(string path)
    {
        var candidate = path;
        for (var i = 2; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-{i}.zip");
        }

        return candidate;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; the next report tries again.
        }
    }
}
