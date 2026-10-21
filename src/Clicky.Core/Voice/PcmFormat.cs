using System.Buffers.Binary;

namespace Clicky.Core.Voice;

/// <summary>How one sample is stored in a capture device's buffer.</summary>
public enum SampleEncoding
{
    /// <summary>32-bit IEEE float, nominally -1..1. The usual WASAPI shared-mode mix format.</summary>
    IeeeFloat,

    /// <summary>16-bit signed little-endian.</summary>
    Pcm16,

    /// <summary>24-bit signed little-endian, packed in 3 bytes.</summary>
    Pcm24,

    /// <summary>32-bit signed little-endian.</summary>
    Pcm32,
}

/// <summary>An interleaved PCM layout, as reported by the capture device.</summary>
public readonly record struct PcmFormat(int SampleRate, int Channels, SampleEncoding Encoding)
{
    public int BytesPerSample => Encoding switch
    {
        SampleEncoding.Pcm16 => 2,
        SampleEncoding.Pcm24 => 3,
        _ => 4,
    };

    public int BlockAlign => BytesPerSample * Channels;

    /// <summary>Throws if the format cannot be decoded.</summary>
    public void Validate()
    {
        if (SampleRate is < 8_000 or > 384_000)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleRate), SampleRate, "Unsupported sample rate.");
        }

        if (Channels is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(Channels), Channels, "Unsupported channel count.");
        }

        if (!Enum.IsDefined(Encoding))
        {
            throw new ArgumentOutOfRangeException(nameof(Encoding), Encoding, "Unsupported sample encoding.");
        }
    }

    public override string ToString() => $"{SampleRate} Hz {Channels} ch {Encoding}";
}

/// <summary>Turns interleaved device PCM into mono float samples.</summary>
public static class PcmDecoder
{
    /// <summary>
    /// Decodes whole frames of <paramref name="data"/> into <paramref name="mono"/>, averaging the
    /// channels. A trailing partial frame is ignored. Returns the number of frames written.
    /// </summary>
    /// <remarks>
    /// Averaging (not summing) keeps a mic array's identical channels at the original level and
    /// can never clip.
    /// </remarks>
    public static int ToMono(ReadOnlySpan<byte> data, PcmFormat format, Span<float> mono)
    {
        var blockAlign = format.BlockAlign;
        var frames = data.Length / blockAlign;
        if (mono.Length < frames)
        {
            throw new ArgumentException("Destination is too small.", nameof(mono));
        }

        var channels = format.Channels;
        var bytesPerSample = format.BytesPerSample;
        var scale = 1f / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * blockAlign;
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += Read(data.Slice(offset + (channel * bytesPerSample), bytesPerSample), format.Encoding);
            }

            mono[frame] = sum * scale;
        }

        return frames;
    }

    private static float Read(ReadOnlySpan<byte> sample, SampleEncoding encoding)
    {
        switch (encoding)
        {
            case SampleEncoding.IeeeFloat:
                var value = BinaryPrimitives.ReadSingleLittleEndian(sample);

                // A NaN from a misbehaving driver would poison every later filter output.
                return float.IsFinite(value) ? value : 0f;
            case SampleEncoding.Pcm16:
                return BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f;
            case SampleEncoding.Pcm24:
                var raw = sample[0] | (sample[1] << 8) | (sample[2] << 16);
                return ((raw << 8) >> 8) / 8388608f;
            case SampleEncoding.Pcm32:
                return BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f;
            default:
                throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null);
        }
    }
}
