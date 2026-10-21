using Clicky.Core.Memory;

namespace Clicky.Tests.Memory;

public sealed class MemoryPromptTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private static MemorySnapshot Snapshot(string profile, string current, DateTimeOffset? currentWritten = null) => new(
        new MemorySet(MemoryDocument.Parse(profile), MemoryDocument.Parse(current)),
        profile,
        current,
        Now,
        currentWritten ?? Now);

    [Fact]
    public void Nothing_remembered_gives_an_empty_block()
    {
        var empty = Snapshot(MemoryTemplates.Empty(MemoryFile.Profile).ToMarkdown(), MemoryTemplates.Empty(MemoryFile.Volatile).ToMarkdown());

        Assert.Equal(string.Empty, MemoryPrompt.Build(empty, MemoryLimits.Default, Now));
    }

    [Fact]
    public void Block_has_profile_and_current_work_without_preamble_or_empty_sections()
    {
        var block = MemoryPrompt.Build(
            Snapshot("# Profile\n<!-- note -->\n## About me\n- Firmware engineer.\n## Habits and tools\n", "## Current project\n- Building M11."),
            MemoryLimits.Default,
            Now);

        Assert.StartsWith("<memory>\n", block, StringComparison.Ordinal);
        Assert.EndsWith("</memory>", block, StringComparison.Ordinal);
        Assert.Contains("<profile>\n## About me\n- Firmware engineer.\n</profile>", block, StringComparison.Ordinal);
        Assert.Contains("<current_work updated=\"2026-10-21\">\n## Current project\n- Building M11.\n</current_work>", block, StringComparison.Ordinal);
        Assert.DoesNotContain("note", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Habits", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Old_current_work_is_flagged_as_possibly_stale()
    {
        var block = MemoryPrompt.Build(Snapshot(string.Empty, "## Current project\n- Old thing.", Now.AddDays(-20)), MemoryLimits.Default, Now);

        Assert.Contains("may be out of date", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Over_budget_whole_sections_are_left_out_never_cut()
    {
        var big = "## Big\n- " + new string('x', 200) + "\n";
        var small = "## Small\n- keep me\n";
        var limits = MemoryLimits.Default with { PromptChars = 100 };

        var block = MemoryPrompt.Build(Snapshot(big + small, string.Empty), limits, Now);

        Assert.DoesNotContain("xxx", block, StringComparison.Ordinal);
        Assert.Contains("## Small\n- keep me\n", block, StringComparison.Ordinal);
        Assert.Contains("(1 more section left out for length.)", block, StringComparison.Ordinal);
    }
}
