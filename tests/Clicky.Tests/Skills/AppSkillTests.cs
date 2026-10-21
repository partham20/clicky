using Clicky.Core.Assistant;
using Clicky.Core.Skills;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Skills;

public sealed class AppSkillParserTests
{
    [Fact]
    public void Reads_the_header_and_body()
    {
        const string text = """
            ---
            name: Excel
            apps: excel.exe, ET
            titles: ["- Excel", "Inbox, Mail"]
            description: Ribbon tips
            ---
            Sort is on the Data tab.

            Freeze Panes is on View.
            """;

        Assert.True(AppSkillParser.TryParse(text, @"C:\skills\excel.md", "excel", out var skill, out var error), error);
        Assert.Equal("Excel", skill!.Name);
        Assert.Equal("Ribbon tips", skill.Description);
        Assert.Equal(["excel", "ET"], skill.Apps);
        Assert.Equal(["- Excel", "Inbox, Mail"], skill.Titles);
        Assert.Equal("Sort is on the Data tab.\n\nFreeze Panes is on View.", skill.Body);
    }

    [Fact]
    public void Reads_block_lists_and_defaults_the_name()
    {
        const string text = "\uFEFF---\r\napps:\r\n  - blender\r\n  - 'blender-launcher.exe'\r\n# a comment\r\n---\r\nPress Tab for edit mode.\r\n";

        Assert.True(AppSkillParser.TryParse(text, "blender.md", "Blender", out var skill, out _));
        Assert.Equal("Blender", skill!.Name);
        Assert.Equal(["blender", "blender-launcher"], skill.Apps);
        Assert.Empty(skill.Titles);
    }

    [Theory]
    [InlineData("Just some notes", "no front matter")]
    [InlineData("---\napps: x\nBody without a closing fence", "not closed")]
    [InlineData("---\nname: Nothing\n---\nBody", "no apps or titles")]
    [InlineData("---\napps: x\n---\n   \n", "empty")]
    public void Rejects_files_it_cannot_use(string text, string reason)
    {
        Assert.False(AppSkillParser.TryParse(text, "x.md", "x", out var skill, out var error));
        Assert.Null(skill);
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Cuts_a_very_long_body()
    {
        var text = "---\napps: x\n---\n" + new string('a', AppSkillParser.MaxBodyChars + 500);
        Assert.True(AppSkillParser.TryParse(text, "x.md", "x", out var skill, out _));
        Assert.EndsWith("[...the rest of this guide was cut]", skill!.Body, StringComparison.Ordinal);
        Assert.True(skill.Body.Length < AppSkillParser.MaxBodyChars + 100);
    }

    [Fact]
    public void Matches_by_process_name_or_title()
    {
        var skill = new AppSkill("Sheets", null, ["excel"], ["- Google Sheets"], "body", "x.md");
        Assert.True(skill.Matches(new ForegroundApp("EXCEL.EXE", "Book1")));
        Assert.True(skill.Matches(new ForegroundApp("chrome.exe", "Budget - Google Sheets - Google Chrome")));
        Assert.False(skill.Matches(new ForegroundApp("excelsior.exe", "Book1")));
        Assert.False(skill.Matches(new ForegroundApp("chrome.exe", "Inbox")));
    }
}

public sealed class AppSkillLibraryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "clicky-skills-" + Guid.NewGuid().ToString("N"));
    private readonly AppSkillLibrary _library;

    public AppSkillLibraryTests()
    {
        _library = new AppSkillLibrary(_directory, NullLogger<AppSkillLibrary>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void A_missing_folder_has_no_skills_and_EnsureCreated_writes_a_readme()
    {
        Assert.Empty(_library.All);

        _library.EnsureCreated();
        Assert.True(File.Exists(Path.Combine(_directory, AppSkillLibrary.ReadmeFileName)));
        Assert.Empty(_library.All);
    }

    [Fact]
    public void Loads_top_level_files_and_skill_folders_and_skips_bad_ones()
    {
        Write("excel.md", "---\napps: excel\n---\nExcel tips");
        Write(Path.Combine("figma", "SKILL.md"), "---\ntitles: \"- Figma\"\n---\nFigma tips");
        Write("notes.md", "no header here");
        Write("readme.md", "---\napps: x\n---\nnot a skill");

        var names = _library.All.Select(s => s.Name).ToArray();
        Assert.Equal(["excel", "figma"], names);
    }

    [Fact]
    public void Picks_up_edits_and_deletes()
    {
        var path = Write("excel.md", "---\napps: excel\n---\nOld");
        Assert.Equal("Old", Assert.Single(_library.All).Body);

        File.WriteAllText(path, "---\napps: excel\n---\nNew and longer");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("New and longer", Assert.Single(_library.All).Body);

        File.Delete(path);
        Assert.Empty(_library.All);
    }

    [Fact]
    public void Each_guide_goes_once_per_conversation_and_again_after_an_edit()
    {
        var path = Write("excel.md", "---\nname: Excel\napps: excel\n---\nSort is on the Data tab.");
        var excel = new ForegroundApp("EXCEL.EXE", "Book1 - Excel");
        var conversation = new Conversation();

        var context = _library.ContextFor(conversation, excel);
        Assert.NotNull(context);
        Assert.StartsWith("[App guide \"Excel\"", context, StringComparison.Ordinal);
        Assert.Contains("Sort is on the Data tab.", context, StringComparison.Ordinal);
        Assert.Null(_library.ContextFor(conversation, excel));
        Assert.Null(_library.ContextFor(conversation, new ForegroundApp("notepad.exe", "x")));
        Assert.NotNull(_library.ContextFor(new Conversation(), excel));

        File.WriteAllText(path, "---\nname: Excel\napps: excel\n---\nFilter is on the Data tab too.");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Contains("Filter", _library.ContextFor(conversation, excel), StringComparison.Ordinal);
    }

    [Fact]
    public void Guides_go_again_after_history_is_trimmed()
    {
        Write("excel.md", "---\napps: excel\n---\nTips");
        var excel = new ForegroundApp("EXCEL.EXE", "Book1");
        var conversation = new Conversation();
        Assert.NotNull(_library.ContextFor(conversation, excel));

        var limits = new HistoryLimits(MaxTurns: 1, TrimSlack: 1);
        conversation.Append(new ConversationTurn("q1", null, "a1", [], DateTimeOffset.UtcNow), limits);
        conversation.Append(new ConversationTurn("q2", null, "a2", [], DateTimeOffset.UtcNow), limits);
        Assert.NotEqual(0, conversation.Generation);

        Assert.NotNull(_library.ContextFor(conversation, excel));
    }

    [Fact]
    public void At_most_three_guides_per_request()
    {
        for (var i = 0; i < 5; i++)
        {
            Write($"s{i}.md", "---\ntitles: Report\n---\nTips " + i);
        }

        Assert.Equal(AppSkillLibrary.MaxPerRequest, _library.Matching(new ForegroundApp("winword.exe", "Report.docx")).Count);
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
}
