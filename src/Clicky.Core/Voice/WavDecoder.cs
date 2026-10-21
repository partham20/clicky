using System.Buffers.Binary;

namespace Clicky.Core.Voice;

/// <summary>Reads the WAV files speech engines produce into mono float samples.</summary>
public static class WavDecoder
{
    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>Decodes a RIFF/WAVE file with PCM 16/24/32-bit or 32-bit float samples, any channel count.</summary>
    /// <exception cref="FormatException">Not a WAV file this can read.</exception>
    public static (float[] Samples, int SampleRate) Decode(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new FormatException("Not a RIFF/WAVE file.");
        }

        PcmFormat? format = null;
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = wav.Slice(offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav[(offset + 4)..]);
            var body = offset + 8;

            if (id.SequenceEqual("fmt "u8))
            {
                format = ReadFormat(wav.Slice(body, (int)Math.Min(size, (uint)(wav.Length - body))));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format is not { } pcm)
                {
                    throw new FormatException("WAV data comes before its format.");
                }

                // Streamed WAVs may leave the size at 0 or 0xFFFFFFFF: read to the end.
                var available = wav.Length - body;
                var length = size == 0 || size > available ? available : (int)size;
                var data = wav.Slice(body, length);
                var samples = new float[data.Length / pcm.BlockAlign];
                PcmDecoder.ToMono(data, pcm, samples);
                return (samples, pcm.SampleRate);
            }

            // Chunks are word-aligned.
            offset = (int)Math.Min(wav.Length, body + (long)size + (size & 1));
        }

        throw new FormatException("WAV file has no data.");
    }

    private static PcmFormat ReadFormat(ReadOnlySpan<byte> fmt)
    {
        if (fmt.Length < 16)
        {
            throw new FormatException("WAV format chunk is too short.");
        }

        var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        var rate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);

        if (tag == FormatExtensible && fmt.Length >= 26)
        {
            // The first two bytes of the sub-format GUID are the real format tag.
            tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
        }

        SampleEncoding? encoding = (tag, bits) switch
        {
            (FormatIeeeFloat, 32) => SampleEncoding.IeeeFloat,
            (FormatPcm, 16) => SampleEncoding.Pcm16,
            (FormatPcm, 24) => SampleEncoding.Pcm24,
            (FormatPcm, 32) => SampleEncoding.Pcm32,
            _ => null,
        };

        if (encoding is null)
        {
            throw new FormatException($"Unsupported WAV encoding (format {tag}, {bits} bits).");
        }

        var format = new PcmFormat(rate, channels, encoding.Value);
        try
        {
            format.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new FormatException($"Unsupported WAV format: {ex.Message}", ex);
        }

        return format;
    }
}
