using Clicky.Core.Voice;

namespace Clicky.Tests.Voice;

public sealed class SpeechGateTests
{
    private static readonly TimeSpan MinSpeech = TimeSpan.FromMilliseconds(300);

    [Fact]
    public void Digital_silence_and_quiet_room_noise_are_not_speech()
    {
        Assert.False(SpeechDetector.Analyze(new float[32_000]).HasSpeech(MinSpeech));
        Assert.False(SpeechDetector.Analyze(Noise(seconds: 2, db: -60)).HasSpeech(MinSpeech));
        Assert.False(SpeechDetector.Analyze([]).HasSpeech(TimeSpan.Zero));
    }

    [Fact]
    public void Steady_fan_noise_is_not_speech_even_when_fairly_loud()
    {
        var analysis = SpeechDetector.Analyze(Noise(seconds: 3, db: -42));

        Assert.False(analysis.HasSpeech(MinSpeech), analysis.ToString());
    }

    [Fact]
    public void Speech_in_noise_is_found_and_trimmed_with_padding()
    {
        // 1 s noise, 1.5 s "speech" at -25 dBFS, 1 s noise.
        var audio = Concat(Noise(1, -55), Tone(1.5, -25), Noise(1, -55));

        var analysis = SpeechDetector.Analyze(audio);

        Assert.True(analysis.HasSpeech(MinSpeech));
        Assert.InRange(analysis.SpeechDuration.TotalMilliseconds, 1480, 1520);
        Assert.Equal(16_000 - 4_000, analysis.Start);
        Assert.Equal(24_000 + 8_000, analysis.Length);
        Assert.InRange(analysis.NoiseFloorDb, -57, -53);
    }

    [Fact]
    public void Talking_from_the_first_to_the_last_sample_still_counts()
    {
        var analysis = SpeechDetector.Analyze(Tone(2, -30));

        Assert.True(analysis.HasSpeech(MinSpeech), analysis.ToString());
        Assert.Equal(0, analysis.Start);
        Assert.Equal(32_000, analysis.Length);
    }

    [Fact]
    public void A_click_is_too_short_to_count()
    {
        var analysis = SpeechDetector.Analyze(Concat(Noise(1, -60), Tone(0.06, -10), Noise(1, -60)));

        Assert.InRange(analysis.SpeechDuration.TotalMilliseconds, 40, 80);
        Assert.False(analysis.HasSpeech(MinSpeech));
    }

    [Theory]
    [InlineData("  Open   the settings page. ", "Open the settings page.")]
    [InlineData("[BLANK_AUDIO] What is this? (keyboard clicking)", "What is this?")]
    [InlineData("♪ Turn on dark mode ♪", "Turn on dark mode")]
    [InlineData("*coughs* Where's the save button?", "Where's the save button?")]
    public void Annotations_are_removed_and_whitespace_tidied(string raw, string expected) =>
        Assert.Equal(expected, TranscriptCleaner.Clean(raw, TimeSpan.FromSeconds(2)));

    [Theory]
    [InlineData("Thanks for watching!")]
    [InlineData("Subtitles by the Amara.org community")]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("(silence)")]
    [InlineData(" . ")]
    public void Subtitle_furniture_is_always_dropped(string raw) =>
        Assert.Equal(string.Empty, TranscriptCleaner.Clean(raw, TimeSpan.FromSeconds(5)));

    [Theory]
    [InlineData("Thank you.")]
    [InlineData("you")]
    [InlineData("Bye!")]
    public void Fillers_are_dropped_only_when_there_was_barely_any_speech(string raw)
    {
        Assert.Equal(string.Empty, TranscriptCleaner.Clean(raw, TimeSpan.FromMilliseconds(400)));
        Assert.Equal(raw.Trim(), TranscriptCleaner.Clean(raw, TimeSpan.FromMilliseconds(900)));
    }

    [Fact]
    public void Keys_ignore_case_punctuation_and_spacing() =>
        Assert.Equal("thank you", TranscriptCleaner.Key("  THANK,   you!! "));

    internal static float[] Tone(double seconds, double db)
    {
        // Sine RMS is amplitude / sqrt 2.
        var amplitude = Math.Pow(10, db / 20) * Math.Sqrt(2);
        return AudioConversionTests.Sine(220, 16_000, seconds, amplitude);
    }

    internal static float[] Noise(double seconds, double db, int seed = 1)
    {
        // Uniform noise in [-a, a] has RMS a / sqrt 3.
        var amplitude = Math.Pow(10, db / 20) * Math.Sqrt(3);
        var random = new Random(seed);
        var samples = new float[(int)(seconds * 16_000)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(((random.NextDouble() * 2) - 1) * amplitude);
        }

        return samples;
    }

    internal static float[] Concat(params float[][] parts) => [.. parts.SelectMany(p => p)];
}
