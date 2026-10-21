using Clicky.Core.Memory;

namespace Clicky.Tests.Memory;

public sealed class MemoryDocumentTests
{
    [Fact]
    public void Parses_preamble_sections_bullets_continuations_and_free_text()
    {
        var doc = MemoryDocument.Parse(
            """
            # Profile
            <!-- instructions
            ## not a section, inside the comment
            -->

            ## About me
            - Firmware engineer at Delta Electronics.
            * Ships WPF and MAUI apps
              on the side.

            ## How to answer
            Keep it short.
            -
            """);

        Assert.StartsWith("# Profile", doc.Preamble, StringComparison.Ordinal);
        Assert.Contains("## not a section", doc.Preamble, StringComparison.Ordinal);
        Assert.Equal(["About me", "How to answer"], doc.Sections.Select(s => s.Heading));
        Assert.Equal(
            [new MemoryEntry("Firmware engineer at Delta Electronics."), new MemoryEntry("Ships WPF and MAUI apps\non the side.")],
            doc.Sections[0].Entries);
        Assert.Equal([new MemoryEntry("Keep it short.", IsBullet: false)], doc.Sections[1].Entries);
    }

    [Fact]
    public void Round_trips_through_markdown()
    {
        var doc = MemoryDocument.Parse("# T\n\n## A\n- one\n- two\n  wrapped\nfree text\n\n## Empty\n");

        var again = MemoryDocument.Parse(doc.ToMarkdown());

        Assert.Equal(doc.Preamble, again.Preamble);
        Assert.Equal(doc.Sections.Select(s => (s.Heading, string.Join("|", s.Entries))), again.Sections.Select(s => (s.Heading, string.Join("|", s.Entries))));
        Assert.Equal("# T\n\n## A\n- one\n- two\n  wrapped\nfree text\n\n## Empty\n", doc.ToMarkdown());
    }

    [Fact]
    public void Templates_are_empty_but_have_their_sections()
    {
        var profile = MemoryDocument.Parse(MemoryTemplates.Empty(MemoryFile.Profile).ToMarkdown());

        Assert.True(profile.IsEmpty);
        Assert.Equal(MemoryTemplates.ProfileSections, profile.Sections.Select(s => s.Heading));
        Assert.Equal(string.Empty, profile.BodyMarkdown());
    }

    [Fact]
    public void Blank_file_is_empty() => Assert.Same(MemoryDocument.Empty, MemoryDocument.Parse("  \n "));
}
