using Clicky.Core.Actions;

namespace Clicky.Tests.Assistant;

public sealed class ActionTagReaderTests
{
    [Fact]
    public void Splits_speech_and_tags_in_one_chunk()
    {
        var reader = new ActionTagReader();

        var tokens = reader.Push("That's Share. <action>{\"type\":\"point\",\"x\":1,\"y\":2}</action> Click it.").Concat(reader.Flush()).ToList();

        Assert.Equal(
            [new(false, "That's Share. "), new(true, "{\"type\":\"point\",\"x\":1,\"y\":2}"), new(false, " Click it.")],
            tokens);
    }

    [Fact]
    public void Tags_split_across_any_chunk_boundary_are_reassembled()
    {
        const string text = "Here. <action>{\"type\":\"key\",\"keys\":\"Ctrl+S\"}</action>Done.";
        for (var split = 1; split < text.Length; split++)
        {
            var reader = new ActionTagReader();
            var tokens = reader.Push(text[..split]).Concat(reader.Push(text[split..])).Concat(reader.Flush()).ToList();

            Assert.Equal("Here. Done.", string.Concat(tokens.Where(t => !t.IsAction).Select(t => t.Text)));
            Assert.Equal(["{\"type\":\"key\",\"keys\":\"Ctrl+S\"}"], tokens.Where(t => t.IsAction).Select(t => t.Text));
        }
    }

    [Fact]
    public void Speech_is_released_at_once_except_a_possible_tag_start()
    {
        var reader = new ActionTagReader();

        Assert.Equal([new TagReaderToken(false, "Hello ")], reader.Push("Hello <ac"));
        Assert.Equal([new TagReaderToken(false, "<ac and more")], reader.Push(" and more"));
        Assert.Equal([new TagReaderToken(false, "3 ")], reader.Push("3 <"));
        Assert.Equal([new TagReaderToken(false, "< 4")], reader.Push(" 4"));
    }

    [Fact]
    public void An_unclosed_tag_is_spoken_at_the_end_rather_than_lost()
    {
        var reader = new ActionTagReader();
        reader.Push("Look <action>{\"type\":");

        Assert.Equal([new TagReaderToken(false, "<action>{\"type\":")], reader.Flush());
    }

    [Fact]
    public void Tag_names_are_case_insensitive_and_whitespace_inside_is_trimmed()
    {
        var reader = new ActionTagReader();

        var tokens = reader.Push("<ACTION>\n {\"type\":\"done\"} \n</Action>").Concat(reader.Flush()).ToList();

        Assert.Equal([new TagReaderToken(true, "{\"type\":\"done\"}")], tokens);
    }
}
