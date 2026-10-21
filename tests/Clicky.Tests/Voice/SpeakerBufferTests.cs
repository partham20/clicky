using System.Buffers.Binary;
using Clicky.Core.Settings;
using Clicky.Core.Voice;

namespace Clicky.Tests.Voice;

public sealed class SpeakerBufferTests
{
    [Fact]
    public void Plays_what_was_written_with_gain_then_silence()
    {
        var buffer = new SpeakerBuffer(1000, gain: 0.5f);
        buffer.Write([1f, 1f, 1f]);

        var output = new float[5];
        Assert.True(buffer.Render(output));

        Assert.Equal([0.5f, 0.5f, 0.5f, 0f, 0f], output);
        Assert.Equal(3, buffer.Played);
        Assert.False(buffer.IsDrained);
    }

    [Fact]
    public void Drains_once_completed_and_played_out()
    {
        var buffer = new SpeakerBuffer(1000);
        buffer.Write(new float[10]);
        buffer.Complete();

        Assert.True(buffer.Render(new float[10]));
        Assert.False(buffer.IsDrained);
        Assert.False(buffer.Render(new float[10]));
        Assert.True(buffer.IsDrained);
    }

    [Fact]
    public void Stop_fades_out_and_drops_the_rest()
    {
        var buffer = new SpeakerBuffer(1000, fade: TimeSpan.FromMilliseconds(10));
        buffer.Write(Enumerable.Repeat(1f, 1000).ToArray());
        buffer.Render(new float[100]);

        buffer.Stop();
        var output = new float[50];
        buffer.Render(output);

        Assert.Equal(1f, output[0]);
        Assert.True(output[1] < output[0] && output[9] < output[1]);
        Assert.All(output[10..], s => Assert.Equal(0f, s));
        Assert.True(buffer.IsDrained);
        Assert.Equal(110, buffer.Played);

        buffer.Write([1f]);
        Assert.Equal(1000, buffer.Written);
    }

    [Fact]
    public async Task Waiters_are_released_when_playback_reaches_them()
    {
        var buffer = new SpeakerBuffer(1000);
        buffer.Write(new float[100]);
        var reached = buffer.WhenPlayed(60);

        buffer.Render(new float[50]);
        Assert.False(reached.IsCompleted);
        buffer.Render(new float[50]);
        await reached.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(buffer.WhenPlayed(10).IsCompleted);
    }

    [Fact]
    public void Starving_after_the_start_counts_as_a_gap()
    {
        var buffer = new SpeakerBuffer(1000);
        buffer.Render(new float[100]);
        Assert.Equal(TimeSpan.Zero, buffer.Underruns);

        buffer.Write(new float[10]);
        buffer.Render(new float[100]);

        Assert.Equal(TimeSpan.FromMilliseconds(90), buffer.Underruns);
    }

    [Fact]
    public void Level_follows_the_audio_and_ends_at_zero()
    {
        var levels = new List<float>();
        var buffer = new SpeakerBuffer(1000);
        buffer.LevelChanged += levels.Add;
        buffer.Write(Enumerable.Repeat(0.2f, 100).ToArray());
        buffer.Complete();

        buffer.Render(new float[50]);
        buffer.Render(new float[50]);
        buffer.Render(new float[50]);

        Assert.True(levels[0] > 0.5f);
        Assert.Equal(0f, levels[^1]);
    }
}

public sealed class SpeechClipTests
{
    [Fact]
    public void Silence_is_trimmed_to_a_small_margin()
    {
        var samples = new float[2000];
        samples[1000] = 0.5f;
        samples[1100] = -0.5f;

        var trimmed = new SpeechClip(samples, 1000, TimeSpan.Zero).TrimSilence();

        // 15 ms margin either side at 1 kHz.
        Assert.Equal(101 + 30, trimmed.Samples.Length);
        Assert.Equal(0.5f, trimmed.Samples[15]);
    }

    [Fact]
    public void All_silence_trims_to_nothing()
    {
        Assert.Empty(new SpeechClip(new float[500], 1000, TimeSpan.Zero).TrimSilence().Samples);
    }
}

public sealed class WavDecoderTests
{
    [Fact]
    public void Reads_back_what_the_encoder_wrote()
    {
        float[] samples = [0f, 0.5f, -0.5f, 0.25f];

        var (decoded, rate) = WavDecoder.Decode(WavEncoder.EncodePcm16(samples, 22_050));

        Assert.Equal(22_050, rate);
        Assert.Equal(samples.Length, decoded.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i], decoded[i], 3);
        }
    }

    [Fact]
    public void Reads_extensible_float_stereo_and_skips_unknown_chunks()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        writer.Write("LIST"u8);
        writer.Write(3);
        writer.Write(new byte[] { 1, 2, 3, 0 });
        writer.Write("fmt "u8);
        writer.Write(40);
        writer.Write((ushort)0xFFFE);
        writer.Write((ushort)2);
        writer.Write(48_000);
        writer.Write(48_000 * 8);
        writer.Write((ushort)8);
        writer.Write((ushort)32);
        writer.Write((ushort)22);
        writer.Write((ushort)32);
        writer.Write(3);
        var subFormat = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(subFormat, 3);
        writer.Write(subFormat);
        writer.Write("data"u8);
        writer.Write(0);
        writer.Write(1f);
        writer.Write(0f);
        writer.Write(-1f);
        writer.Write(-1f);

        var (decoded, rate) = WavDecoder.Decode(stream.ToArray());

        Assert.Equal(48_000, rate);
        Assert.Equal([0.5f, -1f], decoded);
    }

    [Fact]
    public void Rejects_what_is_not_a_wav()
    {
        Assert.Throws<FormatException>(() => WavDecoder.Decode("hello world, not audio"u8));
    }
}

public sealed class QuietPolicyTests
{
    private const QuietReasons Everything = QuietReasons.InCall | QuietReasons.ScreenSharing | QuietReasons.FocusAssist | QuietReasons.FullScreen | QuietReasons.Muted;

    [Fact]
    public void Everything_detected_applies_by_default()
    {
        Assert.Equal(Everything, QuietPolicy.Apply(new SpeechSettings(), Everything));
    }

    [Fact]
    public void Turning_a_check_off_lets_Clicky_speak_through_it()
    {
        var settings = new SpeechSettings(QuietInCalls: false, QuietWhenSharing: false, QuietInFocusAssist: false);

        Assert.Equal(QuietReasons.None, QuietPolicy.Apply(settings, QuietReasons.InCall | QuietReasons.ScreenSharing | QuietReasons.FocusAssist | QuietReasons.FullScreen));
        Assert.Equal(QuietReasons.Muted | QuietReasons.NoOutputDevice, QuietPolicy.Apply(settings, QuietReasons.Muted | QuietReasons.NoOutputDevice));
    }

    [Fact]
    public void Quiet_mode_always_applies_and_is_named_first()
    {
        var quiet = QuietPolicy.Apply(new SpeechSettings(QuietMode: true), QuietReasons.InCall);

        Assert.Equal(QuietReasons.QuietMode | QuietReasons.InCall, quiet);
        Assert.Equal("Quiet mode is on.", QuietPolicy.Describe(quiet));
        Assert.Equal("You seem to be in a call (Microsoft Teams).", QuietPolicy.Describe(QuietReasons.InCall | QuietReasons.FullScreen, "Microsoft Teams"));
    }
}

public sealed class SpeechSettingsTests
{
    [Fact]
    public void A_file_without_a_speech_section_gets_the_defaults()
    {
        var loaded = System.Text.Json.JsonSerializer.Deserialize("""{ "speech": { "quietMode": true, "rate": 9 } }""", SettingsJsonContext.Default.ClickySettings)!.Normalize();

        Assert.True(loaded.Speech.QuietMode);
        Assert.Equal(SpeechSettings.MaxRate, loaded.Speech.Rate);
        Assert.Equal(VoiceOutputEngine.Windows, loaded.Speech.Engine);
        Assert.True(loaded.Speech.QuietInCalls);
        Assert.Empty(loaded.Speech.IgnoreMicApps);
        Assert.Equal(new SpeechSettings(), ClickySettings.Default.Normalize().Speech);
    }

    [Fact]
    public void Equal_lists_compare_equal()
    {
        var a = new SpeechSettings(IgnoreMicApps: ["NVIDIA Broadcast.exe"]).Normalize();
        var b = new SpeechSettings(IgnoreMicApps: ["NVIDIA Broadcast.exe"]).Normalize();

        Assert.Equal(a, b);
        Assert.NotEqual(a, b with { IgnoreMicApps = [] });
    }
}
