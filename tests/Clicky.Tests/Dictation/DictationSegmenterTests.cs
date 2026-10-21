using Clicky.Core.Dictation;
using Clicky.Core.Voice;
using static Clicky.Tests.Voice.SpeechGateTests;

namespace Clicky.Tests.Dictation;

public sealed class DictationSegmenterTests
{
    [Fact]
    public void A_pause_after_a_sentence_cuts_a_chunk_in_the_middle_of_the_pause()
    {
        var segmenter = new DictationSegmenter();
        var audio = Concat(Noise(0.5, -60), Tone(4.5, -25), Noise(1.0, -60), Tone(2, -25));

        var chunks = Feed(segmenter, audio);

        var chunk = Assert.Single(chunks);
        Assert.Equal(TimeSpan.Zero, chunk.Offset);

        // 0.5 s lead-in + 4.5 s speech + half of the 600 ms it took to decide.
        Assert.InRange(chunk.Duration.TotalSeconds, 5.25, 5.35);
        var rest = segmenter.Flush();
        Assert.NotNull(rest);
        Assert.Equal(chunk.Duration, rest.Offset);
        Assert.Equal(audio.Length, chunk.Samples.Length + rest.Samples.Length);
    }

    [Fact]
    public void Short_chunks_wait_for_a_longer_pause()
    {
        var segmenter = new DictationSegmenter();

        // 1 s of speech then 0.8 s of quiet: not enough for a short chunk.
        Assert.Empty(Feed(segmenter, Concat(Tone(1, -25), Noise(0.8, -60))));

        // At 1.2 s of quiet it goes.
        Assert.Single(Feed(segmenter, Noise(0.5, -60)));
    }

    [Fact]
    public void Nonstop_speech_is_cut_before_whispers_window_at_the_quietest_moment()
    {
        var segmenter = new DictationSegmenter();

        // 30 s of speech with a short breath (200 ms dip) at 20 s.
        var audio = Concat(Tone(20, -25), Noise(0.2, -50), Tone(10, -25));

        var chunks = Feed(segmenter, audio);

        var chunk = Assert.Single(chunks);
        Assert.InRange(chunk.Duration.TotalSeconds, 20.0, 20.2);
        Assert.True(chunk.Duration <= TimeSpan.FromSeconds(25));
    }

    [Fact]
    public void Long_silence_is_dropped_as_it_arrives_so_memory_stays_flat()
    {
        var segmenter = new DictationSegmenter();

        Assert.Empty(Feed(segmenter, Noise(120, -62)));

        Assert.True(segmenter.Pending <= SpeechAudio.SampleRate * 10);
        var chunks = Feed(segmenter, Concat(Tone(5, -25), Noise(1, -62)));
        var chunk = Assert.Single(chunks);

        // The chunk starts with at most 10 s of lead-in before the speech.
        Assert.InRange(chunk.Offset.TotalSeconds, 110, 119);
        Assert.InRange(chunk.Duration.TotalSeconds, 5.5, 16);
    }

    [Fact]
    public void Ten_minutes_of_sentences_come_out_as_many_chunks_with_nothing_lost()
    {
        var segmenter = new DictationSegmenter();
        var sentence = Concat(Tone(6, -25), Noise(0.9, -60, seed: 3));
        var total = 0L;
        var chunks = new List<DictationChunk>();
        for (var i = 0; i < 86; i++)
        {
            chunks.AddRange(Feed(segmenter, sentence));
            total += sentence.Length;
        }

        if (segmenter.Flush() is { } last)
        {
            chunks.Add(last);
        }

        Assert.InRange(chunks.Count, 80, 90);
        Assert.Equal(total, chunks.Sum(c => (long)c.Samples.Length));
        Assert.All(chunks, c => Assert.True(c.Duration < TimeSpan.FromSeconds(25)));
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i - 1].Offset + chunks[i - 1].Duration, chunks[i].Offset);
        }
    }

    [Fact]
    public void Flush_returns_null_when_nothing_is_left()
    {
        var segmenter = new DictationSegmenter();
        Assert.Null(segmenter.Flush());
    }

    /// <summary>Feeds audio in 10 ms buffers, as the capture thread does.</summary>
    private static List<DictationChunk> Feed(DictationSegmenter segmenter, float[] audio)
    {
        var chunks = new List<DictationChunk>();
        for (var offset = 0; offset < audio.Length; offset += 160)
        {
            chunks.AddRange(segmenter.Append(audio.AsSpan(offset, Math.Min(160, audio.Length - offset))));
        }

        return chunks;
    }
}
