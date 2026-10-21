using Clicky.Core.Voice;

namespace Clicky.Tests.Voice;

public sealed class SpeechChunkerTests
{
    [Fact]
    public void The_first_sentence_is_cut_after_its_first_comma()
    {
        Assert.Equal(
            [new SpeechChunk("Sure,", false), new SpeechChunk("I can open the voice settings for you right now.", true)],
            SpeechChunker.Split("Sure, I can open the voice settings for you right now.", first: true));
    }

    [Fact]
    public void Without_a_comma_the_first_sentence_is_cut_before_a_joining_word()
    {
        Assert.Equal(
            ["I will open the settings window", "and then pick the voice tab for you."],
            SpeechChunker.Split("I will open the settings window and then pick the voice tab for you.", first: true).Select(c => c.Text));
    }

    [Theory]
    [InlineData("Open Settings.")]
    [InlineData("Click the blue button on the left.")]
    [InlineData("Please wait here quietly for a few more seconds now.")]
    public void Short_sentences_or_ones_without_a_pause_stay_whole(string sentence) =>
        Assert.Equal([new SpeechChunk(sentence, false)], SpeechChunker.Split(sentence, first: true));

    [Fact]
    public void Later_sentences_are_left_whole_unless_they_are_long()
    {
        const string Medium = "Sure, I can open the voice settings for you right now.";
        Assert.Equal([new SpeechChunk(Medium, false)], SpeechChunker.Split(Medium, first: false));

        const string Long = "The voice runs on your processor by default, which keeps things simple, but on a laptop with an NVIDIA card you can switch it to the GPU in settings.";
        Assert.Equal(
            [
                new SpeechChunk("The voice runs on your processor by default,", false),
                new SpeechChunk("which keeps things simple, but on a laptop with an NVIDIA card you can switch it to the GPU in settings.", true),
            ],
            SpeechChunker.Split(Long, first: false));
    }

    [Fact]
    public void Nothing_is_lost_or_reordered()
    {
        const string Text = "First, open Settings; then, on the left, pick Voice: the list there has every voice, sorted by accent, and the button below plays a sample of each one so you can compare them.";
        var chunks = SpeechChunker.Split(Text, first: true);

        Assert.True(chunks.Count > 2);
        Assert.False(chunks[0].ContinuesSentence);
        Assert.All(chunks.Skip(1), c => Assert.True(c.ContinuesSentence));
        Assert.All(chunks.Skip(1), c => Assert.True(c.Text.Split(' ').Length >= SpeechChunker.MinChunkWords));
        Assert.Equal(Text, string.Join(' ', chunks.Select(c => c.Text)));
    }

    [Fact]
    public void Blank_text_has_no_chunks() => Assert.Empty(SpeechChunker.Split("  ", first: true));
}
