using Clicky.Core;
using Clicky.Core.Clickys;

namespace Clicky.Tests.Clickys;

public sealed class ClickyWorkspaceTests : IDisposable
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clicky-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ClickyWorkspace _workspace;

    public ClickyWorkspaceTests()
    {
        _workspace = ClickyWorkspace.For(new ClickyPaths(_dir));
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
    public void Folders_live_under_clickys_by_id()
    {
        Assert.Equal(Path.Combine(Path.GetFullPath(_dir), "clickys"), _workspace.Root);
        Assert.Equal(Path.Combine(_workspace.Root, Id), _workspace.DirectoryFor(Id));
        Assert.False(Directory.Exists(_workspace.DirectoryFor(Id)));

        Assert.Equal(_workspace.DirectoryFor(Id), _workspace.EnsureCreated(Id));
        Assert.True(Directory.Exists(_workspace.DirectoryFor(Id)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("abc/def")]
    [InlineData("C:\\Windows")]
    [InlineData("xyz")]
    public void Non_hex_ids_are_refused(string id)
    {
        Assert.Throws<ArgumentException>(() => _workspace.DirectoryFor(id));
        Assert.Throws<ArgumentException>(() => _workspace.DescribeForPrompt(id));
        Assert.Throws<ArgumentException>(() => _workspace.ListFiles(id));
    }

    [Fact]
    public async Task Saving_refuses_a_bad_id_without_writing_anywhere()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _workspace.SaveAsync("../escape", "x.md", "nope"));
        Assert.False(Directory.Exists(Path.Combine(_dir, "escape")));
    }

    [Fact]
    public void No_folder_or_no_files_describes_as_empty()
    {
        Assert.Empty(_workspace.ListFiles(Id));
        Assert.Equal(string.Empty, _workspace.DescribeForPrompt(Id));

        _workspace.EnsureCreated(Id);
        Assert.Equal(string.Empty, _workspace.DescribeForPrompt(Id));
    }

    [Fact]
    public void Small_text_files_are_included_and_others_only_listed()
    {
        var folder = _workspace.EnsureCreated(Id);
        File.WriteAllText(Path.Combine(folder, "notes.md"), "Project Falcon ships Friday.\n");
        File.WriteAllBytes(Path.Combine(folder, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3]);
        File.WriteAllText(Path.Combine(folder, "huge.txt"), new string('x', ClickyWorkspace.MaxFileChars + 1));
        File.WriteAllText(Path.Combine(folder, "binary.txt"), "has a\0nul");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "deep.csv"), "a,b\n1,2");

        var text = _workspace.DescribeForPrompt(Id);

        Assert.Contains("- notes.md (29 B):\n<file name=\"notes.md\">\nProject Falcon ships Friday.\n</file>", text, StringComparison.Ordinal);
        Assert.Contains("<file name=\"sub/deep.csv\">\na,b\n1,2\n</file>", text, StringComparison.Ordinal);
        Assert.Contains("- logo.png (8 B)\n", text + "\n", StringComparison.Ordinal);
        Assert.DoesNotContain("<file name=\"logo.png\">", text, StringComparison.Ordinal);
        Assert.Matches(@"- huge\.txt \([^)\n]+ KB\), too long to include", text);
        Assert.DoesNotContain("xxxxxxxxxx", text, StringComparison.Ordinal);
        Assert.Contains("- binary.txt (9 B)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<file name=\"binary.txt\">", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Included_text_stays_under_the_total_cap()
    {
        var folder = _workspace.EnsureCreated(Id);
        const int each = 15_000;
        var count = (ClickyWorkspace.MaxTotalChars / each) + 2;
        for (var i = 0; i < count; i++)
        {
            File.WriteAllText(Path.Combine(folder, $"part{i}.txt"), new string((char)('a' + i), each));
        }

        var text = _workspace.DescribeForPrompt(Id);

        var included = Enumerable.Range(0, count).Count(i => text.Contains($"<file name=\"part{i}.txt\">", StringComparison.Ordinal));
        Assert.Equal(ClickyWorkspace.MaxTotalChars / each, included);
        Assert.Equal(count - included, CountOf(text, ", too long to include"));
        Assert.True(text.Length < ClickyWorkspace.MaxTotalChars + 2_000, $"{text.Length} chars");
        for (var i = 0; i < count; i++)
        {
            Assert.Contains($"- part{i}.txt (", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Lists_at_most_the_cap_and_counts_the_rest()
    {
        var folder = _workspace.EnsureCreated(Id);
        for (var i = 0; i < ClickyWorkspace.MaxListedFiles + 3; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"f{i:00}.bin"), [1]);
        }

        var text = _workspace.DescribeForPrompt(Id);

        Assert.Equal(ClickyWorkspace.MaxListedFiles, CountOf(text, ".bin ("));
        Assert.EndsWith("- …and 3 more files", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_list_newest_first_with_relative_names()
    {
        var folder = _workspace.EnsureCreated(Id);
        var old = Path.Combine(folder, "old.txt");
        var recent = Path.Combine(folder, "recent.txt");
        File.WriteAllText(old, "o");
        File.WriteAllText(recent, "r");
        File.SetLastWriteTimeUtc(old, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(recent, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var files = _workspace.ListFiles(Id);

        Assert.Equal(["recent.txt", "old.txt"], files.Select(f => f.Name));
        Assert.Equal(recent, files[0].FullPath);
        Assert.Equal(1, files[0].Length);
    }

    [Fact]
    public async Task Saving_never_overwrites()
    {
        var first = await _workspace.SaveAsync(Id, "report.md", "one");
        var second = await _workspace.SaveAsync(Id, "report.md", "two");
        var third = await _workspace.SaveAsync(Id, "report.md", "three");

        Assert.Equal(Path.Combine(_workspace.DirectoryFor(Id), "report.md"), first);
        Assert.Equal(Path.Combine(_workspace.DirectoryFor(Id), "report (2).md"), second);
        Assert.Equal(Path.Combine(_workspace.DirectoryFor(Id), "report (3).md"), third);
        Assert.Equal("one", await File.ReadAllTextAsync(first));
        Assert.Equal("two", await File.ReadAllTextAsync(second));
        Assert.Equal("three", await File.ReadAllTextAsync(third));
    }

    [Fact]
    public async Task Saved_names_are_made_safe()
    {
        var path = await _workspace.SaveAsync(Id, "../../evil:name?.md", "x");

        Assert.Equal(_workspace.DirectoryFor(Id), Path.GetDirectoryName(path));
        Assert.Equal("reply.md", ClickyWorkspace.SafeFileName("  ..  "));
        Assert.Equal("a-b-c.txt", ClickyWorkspace.SafeFileName("a/b\\c.txt"));
        Assert.Equal(80, ClickyWorkspace.SafeFileName(new string('n', 200)).Length);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
