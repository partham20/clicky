using System.Buffers.Binary;
using Clicky.Core.Voice;

namespace Clicky.Tests.Voice;

public sealed class AudioConversionTests
{
    [Fact]
    public void Float_stereo_is_averaged_to_mono_and_a_partial_frame_is_ignored()
    {
        var data = Floats(0.5f, -0.5f, 1f, 0.5f, float.NaN, 0.25f, 0.9f);

        var mono = new float[8];
        var frames = PcmDecoder.ToMono(data, new PcmFormat(48_000, 2, SampleEncoding.IeeeFloat), mono);

        Assert.Equal(3, frames);
        Assert.Equal([0f, 0.75f, 0.125f], mono[..3]);
    }

    [Fact]
    public void Integer_encodings_scale_to_plus_minus_one()
    {
        var pcm16 = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(pcm16, short.MinValue);
        BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(2), 16384);

        byte[] pcm24 = [0x00, 0x00, 0x80, 0xFF, 0xFF, 0x3F];

        var pcm32 = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(pcm32, int.MinValue / 4);

        var mono = new float[2];
        PcmDecoder.ToMono(pcm16, new PcmFormat(16_000, 1, SampleEncoding.Pcm16), mono);
        Assert.Equal([-1f, 0.5f], mono);

        PcmDecoder.ToMono(pcm24, new PcmFormat(16_000, 1, SampleEncoding.Pcm24), mono);
        Assert.Equal(-1f, mono[0]);
        Assert.Equal(0.5f, mono[1], 4);

        PcmDecoder.ToMono(pcm32, new PcmFormat(16_000, 1, SampleEncoding.Pcm32), mono);
        Assert.Equal(-0.25f, mono[0]);
    }

    [Theory]
    [InlineData(48_000, 48_000)]
    [InlineData(44_100, 44_100)]
    [InlineData(48_000, 47_999)]
    [InlineData(96_000, 12_345)]
    [InlineData(8_000, 8_000)]
    [InlineData(16_000, 16_000)]
    public void Output_length_is_input_times_ratio_rounded_up(int inRate, int inputSamples)
    {
        var resampler = new StreamingResampler(inRate, 16_000);
        var output = new SampleBuffer();

        resampler.Process(new float[inputSamples], output);
        resampler.Flush(output);

        Assert.Equal((int)Math.Ceiling((double)inputSamples * 16_000 / inRate), output.Count);
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    [InlineData(32_000)]
    [InlineData(8_000)]
    public void A_speech_band_tone_keeps_its_level_and_pitch(int inRate)
    {
        const double Frequency = 1000;
        var input = Sine(Frequency, inRate, seconds: 1, amplitude: 0.5);

        var output = Resample(input, inRate);

        // Ignore the edges, where the filter sees the stream start and end.
        var middle = output.AsSpan(800, 14_400);
        Assert.InRange(Rms(middle), 0.5 / Math.Sqrt(2) * 0.99, 0.5 / Math.Sqrt(2) * 1.01);

        var expected = Sine(Frequency, 16_000, seconds: 1, amplitude: 0.5).AsSpan(800, 14_400);
        for (var i = 0; i < middle.Length; i++)
        {
            Assert.True(Math.Abs(middle[i] - expected[i]) < 0.01, $"sample {i}: {middle[i]} vs {expected[i]}");
        }
    }

    [Theory]
    [InlineData(48_000, 12_000)]
    [InlineData(48_000, 9_000)]
    [InlineData(44_100, 15_000)]
    public void Tones_above_the_new_nyquist_do_not_fold_back(int inRate, double frequency)
    {
        var input = Sine(frequency, inRate, seconds: 1, amplitude: 0.5);

        var output = Resample(input, inRate);

        var attenuationDb = 20 * Math.Log10(Rms(output.AsSpan(800, 14_400)) / (0.5 / Math.Sqrt(2)));
        Assert.True(attenuationDb < -60, $"only {attenuationDb:0.0} dB down");
    }

    [Fact]
    public void Output_does_not_depend_on_chunking()
    {
        var input = Sine(440, 44_100, seconds: 0.5, amplitude: 0.8);
        var whole = Resample(input, 44_100);

        var random = new Random(7);
        var resampler = new StreamingResampler(44_100, 16_000);
        var chunked = new SampleBuffer();
        for (var offset = 0; offset < input.Length;)
        {
            var size = Math.Min(random.Next(1, 2000), input.Length - offset);
            resampler.Process(input.AsSpan(offset, size), chunked);
            offset += size;
        }

        resampler.Flush(chunked);
        Assert.Equal(whole, chunked.ToArray());
    }

    [Fact]
    public void Sink_turns_48k_stereo_float_into_16k_mono_and_reports_level_every_50_ms()
    {
        var format = new PcmFormat(48_000, 2, SampleEncoding.IeeeFloat);
        var sink = new SpeechAudioSink(format, TimeSpan.FromSeconds(10));
        var levels = new List<float>();
        sink.LevelChanged += levels.Add;

        var tone = Sine(1000, 48_000, seconds: 1, amplitude: 0.1);
        var stereo = new float[tone.Length * 2];
        for (var i = 0; i < tone.Length; i++)
        {
            stereo[2 * i] = tone[i];
            stereo[(2 * i) + 1] = tone[i];
        }

        var bytes = Floats(stereo);
        for (var offset = 0; offset < bytes.Length; offset += 3840)
        {
            sink.Write(bytes.AsSpan(offset, Math.Min(3840, bytes.Length - offset)));
        }

        var speech = sink.Complete();

        Assert.Equal(16_000, speech.Length);
        Assert.Equal(20, levels.Count);

        // 0.1 amplitude sine = -23 dBFS RMS.
        Assert.All(levels, level => Assert.Equal(SpeechAudioSink.LevelFromDb(-23.01), level, 2));
        Assert.False(sink.Truncated);
    }

    [Fact]
    public void Sink_stops_keeping_audio_at_the_maximum_length()
    {
        var sink = new SpeechAudioSink(new PcmFormat(16_000, 1, SampleEncoding.IeeeFloat), TimeSpan.FromSeconds(5));
        var second = Floats(new float[16_000]);

        for (var i = 0; i < 8; i++)
        {
            sink.Write(second);
        }

        Assert.True(sink.Truncated);
        Assert.Equal(5 * 16_000, sink.Complete().Length);
    }

    [Fact]
    public void Wav_header_and_samples_are_16_bit_pcm()
    {
        var wav = WavEncoder.EncodePcm16([0f, 1f, -1f, 2f, 0.5f]);

        Assert.Equal(44 + 10, wav.Length);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal("WAVEfmt "u8.ToArray(), wav[8..16]);
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));

        short[] samples = [.. Enumerable.Range(0, 5).Select(i => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + (2 * i))))];
        Assert.Equal([0, 32767, -32767, 32767, 16384], samples);
    }

    internal static float[] Sine(double frequency, int rate, double seconds, double amplitude)
    {
        var samples = new float[(int)(rate * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate));
        }

        return samples;
    }

    internal static byte[] Floats(params float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), samples[i]);
        }

        return bytes;
    }

    private static float[] Resample(float[] input, int inRate)
    {
        var resampler = new StreamingResampler(inRate, 16_000);
        var output = new SampleBuffer();
        resampler.Process(input, output);
        resampler.Flush(output);
        return output.ToArray();
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        var sum = 0.0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }
}
