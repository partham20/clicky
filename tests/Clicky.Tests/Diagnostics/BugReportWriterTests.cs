using System.IO.Compression;
using Clicky.Core;
using Clicky.Core.Diagnostics;
using Clicky.Tests.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Diagnostics;

public sealed class BugReportWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clicky-tests", Guid.NewGuid().ToString("N"));
    private readonly ClickyPaths _paths;
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero));

    public BugReportWriterTests()
    {
        _paths = new ClickyPaths(_dir);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Report_holds_description_system_info_logs_and_settings()
    {
        File.WriteAllText(Path.Combine(_paths.LogsDirectory, "clicky-20261020.log"), "older line\n");
        File.WriteAllText(Path.Combine(_paths.LogsDirectory, "clicky-20261021.log"), @"key sk-ant-api03-secretsecret in C:\Users\jane\x" + "\n");
        File.WriteAllText(_paths.SettingsFile, "{ \"general\": { \"startWithWindows\": true } }");
        Directory.CreateDirectory(_paths.MemoryDirectory);
        File.WriteAllText(Path.Combine(_paths.MemoryDirectory, "PROFILE.md"), "private");

        var report = await Writer().WriteAsync(new BugReportRequest("The buddy froze\nafter Talk")
        {
            SystemInfo = [new("Clicky", "0.1.0 (installed)"), new("Windows", "Windows 11 Pro 23H2")],
        });

        Assert.StartsWith(_paths.BugReportsDirectory, report.ZipPath, StringComparison.Ordinal);
        Assert.Equal(2, report.LogFiles);
        Assert.Contains("The buddy froze", report.Summary, StringComparison.Ordinal);
        Assert.Contains("- Windows: Windows 11 Pro 23H2", report.Summary, StringComparison.Ordinal);

        var entries = Read(report.ZipPath);
        Assert.Equal(["logs/clicky-20261020.log", "logs/clicky-20261021.log", "report.md", "settings.json"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(@"key [redacted] in C:\Users\<user>\x" + "\n", entries["logs/clicky-20261021.log"]);
        Assert.Contains("startWithWindows", entries["settings.json"], StringComparison.Ordinal);
        Assert.False(File.Exists(report.ZipPath + ".partial"));
    }

    [Fact]
    public async Task Logs_and_settings_can_be_left_out()
    {
        File.WriteAllText(Path.Combine(_paths.LogsDirectory, "clicky-20261021.log"), "line\n");
        File.WriteAllText(_paths.SettingsFile, "{}");

        var report = await Writer().WriteAsync(new BugReportRequest(string.Empty, IncludeLogs: false, IncludeSettings: false));

        Assert.Equal(["report.md"], Read(report.ZipPath).Keys);
        Assert.Contains("(no description)", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_live_log_can_be_read_while_it_is_being_written()
    {
        var path = Path.Combine(_paths.LogsDirectory, "clicky-20261021.log");
        await using var live = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await live.WriteAsync("still writing\n"u8.ToArray());
        await live.FlushAsync();

        var report = await Writer().WriteAsync(new BugReportRequest("x"));

        Assert.Equal("still writing\n", Read(report.ZipPath)["logs/clicky-20261021.log"]);
    }

    [Fact]
    public async Task An_oversized_log_keeps_its_end()
    {
        var path = Path.Combine(_paths.LogsDirectory, "clicky-20261021.log");
        var line = new string('a', 99) + "\n";
        await using (var writer = new StreamWriter(path))
        {
            for (var i = 0; i < (BugReportWriter.MaxLogBytes / 100) + 50; i++)
            {
                await writer.WriteAsync(line);
            }

            await writer.WriteAsync("LAST LINE\n");
        }

        var report = await Writer().WriteAsync(new BugReportRequest("x"));
        var log = Read(report.ZipPath)["logs/clicky-20261021.log"];

        Assert.StartsWith("[... first ", log, StringComparison.Ordinal);
        Assert.EndsWith("LAST LINE\n", log, StringComparison.Ordinal);
        Assert.True(log.Length <= BugReportWriter.MaxLogBytes + 100);
    }

    [Fact]
    public async Task Only_the_newest_reports_are_kept()
    {
        var writer = Writer();
        var paths = new List<string>();
        for (var i = 0; i < BugReportWriter.KeepReports + 2; i++)
        {
            paths.Add((await writer.WriteAsync(new BugReportRequest($"report {i}"))).ZipPath);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var left = Directory.GetFiles(_paths.BugReportsDirectory, "*.zip").Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(paths.TakeLast(BugReportWriter.KeepReports).Select(Path.GetFileName), left);
    }

    [Fact]
    public async Task Two_reports_in_the_same_second_get_different_names()
    {
        var writer = Writer();
        var first = await writer.WriteAsync(new BugReportRequest("a"));
        var second = await writer.WriteAsync(new BugReportRequest("b"));

        Assert.NotEqual(first.ZipPath, second.ZipPath);
        Assert.True(File.Exists(first.ZipPath));
    }

    [Fact]
    public void Issue_link_carries_title_and_body()
    {
        var report = new BugReport(@"C:\x\clicky-bug-20261021-093000.zip", "## What happened\n\nIt crashed & burned", 2);

        var uri = GitHubIssueLink.Create("https://github.com/partham20/clicky/", report, "It crashed & burned\nmore detail");

        Assert.StartsWith("https://github.com/partham20/clicky/issues/new?labels=bug&title=It%20crashed%20%26%20burned&body=", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("clicky-bug-20261021-093000.zip", Uri.UnescapeDataString(uri.Query), StringComparison.Ordinal);
    }

    [Fact]
    public void Issue_link_stays_short()
    {
        var report = new BugReport("r.zip", new string('x', 20_000), 0);

        var uri = GitHubIssueLink.Create("https://github.com/o/r", report, new string('t', 300));

        Assert.True(uri.AbsoluteUri.Length < 8000);
        Assert.Equal(GitHubIssueLink.MaxTitleLength, GitHubIssueLink.Title(new string('t', 300)).Length);
        Assert.Equal("Bug report", GitHubIssueLink.Title("  \n "));
    }

    private BugReportWriter Writer() => new(_paths, NullLogger<BugReportWriter>.Instance, _time);

    private static Dictionary<string, string> Read(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }
}
