using Clicky.Core.Dictation;

namespace Clicky.Tests.Dictation;

public sealed class DictationTextTests
{
    [Theory]
    [InlineData("Dear Sam. New line. Thanks for the notes.", "Dear Sam.\nThanks for the notes.")]
    [InlineData("first point, new line, second point", "first point\nSecond point")]
    [InlineData("That's all. New paragraph. Next topic.", "That's all.\n\nNext topic.")]
    [InlineData("A new line of products", "A new line of products")]
    [InlineData("Thanks, new line", "Thanks\n")]
    [InlineData("No commands here.", "No commands here.")]
    public void Spoken_commands_become_line_breaks(string input, string expected) =>
        Assert.Equal(expected, DictationText.ApplySpokenCommands(input));

    [Theory]
    [InlineData("It works — mostly.", "It works, mostly.")]
    [InlineData("It works—mostly.", "It works, mostly.")]
    [InlineData("Wait — what?", "Wait, what?")]
    [InlineData("Pages 10–12", "Pages 10–12")]
    public void Dashes_become_commas(string input, string expected) =>
        Assert.Equal(expected, DictationText.RemoveDashes(input));

    [Theory]
    [InlineData("git status\nrm -rf /", "git status rm -rf /")]
    [InlineData("one\r\n\r\ntwo\n", "one two")]
    [InlineData("  no breaks  ", "no breaks")]
    public void Terminals_never_get_a_line_break(string input, string expected)
    {
        var result = DictationText.ForTerminal(input);
        Assert.Equal(expected, result);
        Assert.DoesNotContain('\n', result);
        Assert.DoesNotContain('\r', result);
    }

    [Theory]
    [InlineData("world", 'o', " world")]
    [InlineData("world", ' ', "world")]
    [InlineData("world", '\n', "world")]
    [InlineData("world", '(', "world")]
    [InlineData("world", null, "world")]
    [InlineData(", and more", 'o', ", and more")]
    [InlineData("\nNext", 'o', "\nNext")]
    public void Spacing_depends_on_the_character_before_the_caret(string text, char? before, string expected) =>
        Assert.Equal(expected, DictationText.Spaced(text, before));

    [Fact]
    public void Chunks_join_with_one_space_or_none_after_a_break()
    {
        Assert.Equal("One. Two.", DictationText.Join("One.", "Two."));
        Assert.Equal("One.\nTwo.", DictationText.Join("One.\n", "Two."));
        Assert.Equal("Two.", DictationText.Join(string.Empty, "Two."));
    }
}
