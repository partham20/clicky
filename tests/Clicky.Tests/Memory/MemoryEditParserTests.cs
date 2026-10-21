using Clicky.Core.Memory;

namespace Clicky.Tests.Memory;

public sealed class MemoryEditParserTests
{
    [Fact]
    public void Parses_all_three_ops_inside_a_code_fence()
    {
        var edits = MemoryEditParser.Parse(
            """
            Here you go:
            ```json
            {"edits":[
              {"op":"add","file":"profile","section":"How to answer","text":"- Keep answers short."},
              {"op":"update","file":"VOLATILE.md","section":"Current project","old":"M10","text":"M11"},
              {"op":"delete","file":"volatile","section":"Open threads","old":"Check CI"},
            ]}
            ```
            """);

        Assert.Equal(
            [
                new MemoryEdit(MemoryEditKind.Add, MemoryFile.Profile, "How to answer", Text: "Keep answers short."),
                new MemoryEdit(MemoryEditKind.Replace, MemoryFile.Volatile, "Current project", "M10", "M11"),
                new MemoryEdit(MemoryEditKind.Remove, MemoryFile.Volatile, "Open threads", "Check CI"),
            ],
            edits);
    }

    [Fact]
    public void Skips_incomplete_or_unknown_edits()
    {
        var edits = MemoryEditParser.Parse(
            """{"edits":[{"op":"add","file":"profile","section":"A"},{"op":"rename","file":"profile","section":"A","text":"x"},{"op":"add","file":"diary","section":"A","text":"x"},{"op":"replace","file":"profile","section":"A","text":"x"},null,{"op":"add","file":"profile","section":"A","text":"ok"}]}""");

        Assert.Equal([new MemoryEdit(MemoryEditKind.Add, MemoryFile.Profile, "A", Text: "ok")], edits);
    }

    [Fact]
    public void Empty_list_is_fine() => Assert.Empty(MemoryEditParser.Parse("{\"edits\": []}"));

    [Theory]
    [InlineData("No changes needed.")]
    [InlineData("{\"changes\": []}")]
    [InlineData("{\"edits\": [ {\"op\": }")]
    public void Unusable_replies_throw_format_exception(string reply) =>
        Assert.Throws<FormatException>(() => MemoryEditParser.Parse(reply));
}
