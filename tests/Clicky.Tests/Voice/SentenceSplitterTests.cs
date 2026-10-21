using Clicky.Core.Voice;

namespace Clicky.Tests.Voice;

public sealed class SentenceSplitterTests
{
    [Theory]
    [InlineData("Open Settings. Then click Voice.", new[] { "Open Settings.", "Then click Voice." })]
    [InlineData("Really? Yes! Done.", new[] { "Really?", "Yes!", "Done." })]
    [InlineData("He said \"stop.\" Then left.", new[] { "He said \"stop.\"", "Then left." })]
    [InlineData("Pi is 3.14 and v1.2 shipped. Next.", new[] { "Pi is 3.14 and v1.2 shipped.", "Next." })]
    [InlineData("See docs.claude.com for more. Bye.", new[] { "See docs.claude.com for more.", "Bye." })]
    [InlineData("Use a tool, e.g. the pen. Ask Dr. Smith. Done.", new[] { "Use a tool, e.g. the pen.", "Ask Dr. Smith.", "Done." })]
    [InlineData("Ask J. Smith about it. Okay.", new[] { "Ask J. Smith about it.", "Okay." })]
    [InlineData("Wait... what is that? Hmm... Let me look.", new[] { "Wait... what is that?", "Hmm...", "Let me look." })]
    [InlineData("Pens, pencils, etc. are here. Files, etc. Next one.", new[] { "Pens, pencils, etc. are here.", "Files, etc.", "Next one." })]
    [InlineData("It ships in version 2. Then 3.", new[] { "It ships in version 2.", "Then 3." })]
    [InlineData("Done (finally). Next.", new[] { "Done (finally).", "Next." })]
    [InlineData("**Done.** Next.", new[] { "**Done.**", "Next." })]
    [InlineData("No full stop at the end", new[] { "No full stop at the end" })]
    public void Splits_at_sentence_ends_only(string text, string[] expected)
    {
        Assert.Equal(expected, SplitWhole(text));
    }

    [Fact]
    public void Every_line_is_its_own_sentence_and_list_numbers_do_not_split()
    {
        var text = "Here is how:\n1. Open Settings\n2. Click Voice. Pick one.\n\n- Done";

        Assert.Equal(["Here is how:", "1. Open Settings", "2. Click Voice.", "Pick one.", "- Done"], SplitWhole(text));
    }

    [Fact]
    public void Code_blocks_are_dropped_and_inline_code_never_splits()
    {
        var text = "Run this:\n```powershell\nGet-Item . Then this. And that!\n```\nThe `a. B` call works. Bye.";

        Assert.Equal(["Run this:", "The `a. B` call works.", "Bye."], SplitWhole(text));
    }

    [Fact]
    public void An_unclosed_code_block_drops_the_rest()
    {
        Assert.Equal(["Look:"], SplitWhole("Look:\n~~~\nsecret. code. here"));
    }

    [Fact]
    public void A_long_run_without_a_full_stop_is_cut_at_a_comma()
    {
        var clause = new string('a', SentenceSplitter.SoftLimit) + ", and then the rest of it. End.";

        var sentences = SplitWhole(clause);

        Assert.Equal(3, sentences.Count);
        Assert.EndsWith(",", sentences[0], StringComparison.Ordinal);
        Assert.Equal("and then the rest of it.", sentences[1]);
    }

    [Fact]
    public void A_sentence_comes_out_as_soon_as_the_next_one_starts()
    {
        var splitter = new SentenceSplitter();

        Assert.Empty(splitter.Append("Open Settings"));
        Assert.Empty(splitter.Append("."));
        Assert.Equal(["Open Settings."], splitter.Append(" "));
        Assert.Empty(splitter.Append("Then"));
        Assert.Equal(["Then"], splitter.Complete());
    }

    [Fact]
    public void An_ellipsis_waits_for_the_next_word_to_decide()
    {
        var splitter = new SentenceSplitter();

        Assert.Empty(splitter.Append("Hmm... "));
        Assert.Equal(["Hmm..."], splitter.Append("L"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void Output_does_not_depend_on_chunk_size(int chunk)
    {
        const string text = "Hi there! Use e.g. the pen... Then 3.14 works.\n```\nx. y. z\n```\n- **Bold.** Item two? Yes. Etc. Done, etc. ok.";
        var whole = SplitWhole(text);

        var splitter = new SentenceSplitter();
        var pieces = new List<string>();
        for (var i = 0; i < text.Length; i += chunk)
        {
            pieces.AddRange(splitter.Append(text.Substring(i, Math.Min(chunk, text.Length - i))));
        }

        pieces.AddRange(splitter.Complete());
        Assert.Equal(whole, pieces);
    }

    [Fact]
    public void Complete_resets_for_the_next_reply()
    {
        var splitter = new SentenceSplitter();
        splitter.Append("```\ncode");
        Assert.Empty(splitter.Complete());

        Assert.Equal(["Fresh."], [.. splitter.Append("Fresh. "), .. splitter.Complete()]);
    }

    private static List<string> SplitWhole(string text)
    {
        var splitter = new SentenceSplitter();
        return [.. splitter.Append(text), .. splitter.Complete()];
    }
}

public sealed class SpeakableTextTests
{
    [Theory]
    [InlineData("**Open** the *Voice* tab.", "Open the Voice tab.")]
    [InlineData("Click [Export](https://example.com/a(b)) now.", "Click Export now.")]
    [InlineData("See https://www.docs.claude.com/en/api?x=1.", "See docs.claude.com.")]
    [InlineData("Run `dotnet build` first.", "Run dotnet build first.")]
    [InlineData("## Step two", "Step two")]
    [InlineData("- [x] Done item", "Done item")]
    [InlineData("> 1. Quoted step", "Quoted step")]
    [InlineData("| Name | Size |", "Name, Size")]
    [InlineData("|---|:---:|", "")]
    [InlineData("---", "")]
    [InlineData("Great job! 🎉👍🏽", "Great job!")]
    [InlineData("snake_case_name stays", "snake_case_name stays")]
    [InlineData("Press <kbd>Ctrl</kbd>+C", "Press Ctrl +C")]
    [InlineData("~~old~~ new", "old new")]
    [InlineData("  *  ", "")]
    [InlineData("![diagram](x.png)", "diagram")]
    public void Strips_markdown_for_speech(string text, string expected)
    {
        Assert.Equal(expected, SpeakableText.Clean(text));
    }
}
