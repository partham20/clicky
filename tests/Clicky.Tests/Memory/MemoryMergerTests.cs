using Clicky.Core.Memory;

namespace Clicky.Tests.Memory;

public sealed class MemoryMergerTests
{
    private static readonly MemorySet Start = new(
        MemoryDocument.Parse("# Profile\n\n## About me\n- Firmware engineer.\n\n## How to answer\n- Explain things in detail.\n"),
        MemoryDocument.Parse("# Current work\n\n## Current project\n- Building M11.\n"));

    private static MergeResult Apply(params MemoryEdit[] edits) => MemoryMerger.Apply(Start, edits, MemoryLimits.Default);

    [Fact]
    public void Add_appends_to_the_named_section_matching_case_insensitively()
    {
        var result = Apply(new MemoryEdit(MemoryEditKind.Add, MemoryFile.Profile, "about ME", Text: "Uses Visual Studio."));

        Assert.Single(result.Applied);
        Assert.Equal(["Firmware engineer.", "Uses Visual Studio."], result.Memory.Profile.Find("About me")!.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Add_creates_a_missing_section()
    {
        var result = Apply(new MemoryEdit(MemoryEditKind.Add, MemoryFile.Volatile, "Open threads", Text: "Check the CI run."));

        Assert.Equal(["Check the CI run."], result.Memory.Volatile.Find("Open threads")!.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Add_of_an_existing_fact_anywhere_in_the_file_is_a_no_op()
    {
        var result = Apply(new MemoryEdit(MemoryEditKind.Add, MemoryFile.Profile, "Habits", Text: "  firmware   ENGINEER "));

        Assert.Empty(result.Applied);
        Assert.Empty(result.Rejected);
        Assert.Same(Start, result.Memory);
    }

    [Fact]
    public void Replace_swaps_the_matching_entry_even_in_another_section()
    {
        var result = Apply(new MemoryEdit(MemoryEditKind.Replace, MemoryFile.Profile, "About me", Old: "explain things in detail", Text: "Keep spoken answers to one or two sentences."));

        Assert.Single(result.Applied);
        Assert.Equal(["Keep spoken answers to one or two sentences."], result.Memory.Profile.Find("How to answer")!.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Replace_or_remove_of_an_unknown_entry_is_refused_not_guessed()
    {
        var result = Apply(
            new MemoryEdit(MemoryEditKind.Replace, MemoryFile.Profile, "How to answer", Old: "Explain things", Text: "x"),
            new MemoryEdit(MemoryEditKind.Remove, MemoryFile.Volatile, "Current project", Old: "Building M12."));

        Assert.Empty(result.Applied);
        Assert.Equal(2, result.Rejected.Count);
        Assert.Same(Start, result.Memory);
    }

    [Fact]
    public void Remove_deletes_the_entry()
    {
        var result = Apply(new MemoryEdit(MemoryEditKind.Remove, MemoryFile.Volatile, "Current project", Old: "Building M11"));

        Assert.True(result.Memory.Volatile.IsEmpty);
        Assert.True(result.Changed(MemoryFile.Volatile));
        Assert.False(result.Changed(MemoryFile.Profile));
    }

    [Fact]
    public void Long_entries_are_refused_whole_never_shortened()
    {
        var text = new string('a', 150) + " " + new string('b', 150);

        var result = Apply(new MemoryEdit(MemoryEditKind.Add, MemoryFile.Profile, "About me", Text: text));

        Assert.Empty(result.Applied);
        Assert.Contains("longer than", Assert.Single(result.Rejected).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Growth_past_the_file_cap_is_refused_but_removals_still_work_when_over()
    {
        var limits = MemoryLimits.Default with { VolatileChars = 60 };
        var full = MemoryMerger.Apply(
            Start,
            [new MemoryEdit(MemoryEditKind.Add, MemoryFile.Volatile, "Current project", Text: "Second fact that is fairly long.")],
            limits);
        Assert.Contains("VOLATILE.md would exceed 60", Assert.Single(full.Rejected).Reason, StringComparison.Ordinal);

        var overCap = Start.With(MemoryFile.Volatile, MemoryDocument.Parse("## Current project\n- " + new string('x', 100) + "\n- small\n"));
        var trimmed = MemoryMerger.Apply(overCap, [new MemoryEdit(MemoryEditKind.Remove, MemoryFile.Volatile, "Current project", Old: "small")], limits);
        Assert.Single(trimmed.Applied);
    }

    [Theory]
    [InlineData("My OpenAI key is sk-proj-abcdefghijklmnop1234")]
    [InlineData("Wi-Fi password: hunter22")]
    [InlineData("GitHub token ghp_0123456789abcdefghijABCDEFGHIJ")]
    [InlineData("Session eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.sig")]
    public void Secrets_are_refused(string text)
    {
        var result = Apply(new MemoryEdit(MemoryEditKind.Add, MemoryFile.Profile, "About me", Text: text));

        Assert.Equal("looks like a secret", Assert.Single(result.Rejected).Reason);
    }

    [Theory]
    [InlineData("Keep spoken answers to one or two sentences.")]
    [InlineData("Works in C:\\clicky\\clicky on the Clicky repo (github.com/partham20/clicky).")]
    [InlineData("Uses a PIN pad on the Delta test bench.")]
    public void Ordinary_facts_are_not_mistaken_for_secrets(string text) => Assert.False(MemoryMerger.LooksSecret(text));

    [Fact]
    public void Edits_per_pass_are_capped()
    {
        var edits = Enumerable.Range(0, 15).Select(i => new MemoryEdit(MemoryEditKind.Add, MemoryFile.Profile, "Habits", Text: $"Fact {i}.")).ToArray();

        var result = Apply(edits);

        Assert.Equal(MemoryLimits.Default.EditsPerPass, result.Applied.Count);
        Assert.Equal(3, result.Rejected.Count);
    }
}
