namespace Clicky.Core.Voice;

/// <summary>What <see cref="SpeechDetector"/> found in a recording.</summary>
/// <param name="SpeechDuration">Total time in frames that looked like speech.</param>
/// <param name="Start">First sample to keep (speech onset minus padding).</param>
/// <param name="Length">Samples to keep from <paramref name="Start"/>; 0 when there was no speech.</param>
/// <param name="NoiseFloorDb">Estimated background level, dBFS.</param>
/// <param name="PeakDb">Loudest 20 ms frame, dBFS.</param>
public readonly record struct SpeechAnalysis(TimeSpan SpeechDuration, int Start, int Length, double NoiseFloorDb, double PeakDb)
{
    public bool HasSpeech(TimeSpan minimum) => Length > 0 && SpeechDuration >= minimum;

    public override string ToString() =>
        $"speech {SpeechDuration.TotalMilliseconds:0} ms, noise {NoiseFloorDb:0} dBFS, peak {PeakDb:0} dBFS";
}

/// <summary>
/// A plain energy gate over 16 kHz audio: is there enough speech to be worth transcribing, and
/// where does it start and stop. Its main job is keeping silence away from Whisper, which turns
/// silence into "Thank you." and similar.
/// </summary>
/// <remarks>
/// A frame counts as speech when it is well above the recording's own noise floor (its quietest
/// tenth) and above an absolute floor, or simply loud. Relative-to-floor works across mics with
/// very different gain, and an absolute floor stops a dead-quiet recording from treating its own
/// hiss as speech.
/// </remarks>
public static class SpeechDetector
{
    public const int FrameSamples = SpeechAudio.SampleRate / 50;

    /// <summary>How far above the noise floor a frame must be.</summary>
    public const double MarginDb = 12;

    /// <summary>Frames quieter than this are never speech.</summary>
    public const double AbsoluteFloorDb = -50;

    /// <summary>
    /// Frames louder than this always are. Without a cap, a recording that is speech from end to
    /// end (talking from the moment the key goes down) would take its own speech as the noise floor.
    /// </summary>
    public const double CertainSpeechDb = -38;

    /// <summary>Silence kept either side of the speech, so trimming never clips a soft onset or tail.</summary>
    public static readonly TimeSpan Padding = TimeSpan.FromMilliseconds(250);

    public static SpeechAnalysis Analyze(ReadOnlySpan<float> samples)
    {
        var frameCount = samples.Length / FrameSamples;
        if (frameCount == 0)
        {
            return new SpeechAnalysis(TimeSpan.Zero, 0, 0, double.NegativeInfinity, double.NegativeInfinity);
        }

        var energies = new double[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sum = 0.0;
            foreach (var sample in samples.Slice(frame * FrameSamples, FrameSamples))
            {
                sum += sample * sample;
            }

            energies[frame] = 10 * Math.Log10(Math.Max(sum / FrameSamples, 1e-12));
        }

        var sorted = (double[])energies.Clone();
        Array.Sort(sorted);
        var noiseFloor = sorted[(int)(sorted.Length * 0.1)];
        var peak = sorted[^1];
        var threshold = Math.Clamp(noiseFloor + MarginDb, AbsoluteFloorDb, CertainSpeechDb);

        var speechFrames = 0;
        var first = -1;
        var last = -1;
        for (var frame = 0; frame < frameCount; frame++)
        {
            if (energies[frame] < threshold)
            {
                continue;
            }

            speechFrames++;
            if (first < 0)
            {
                first = frame;
            }

            last = frame;
        }

        var speech = TimeSpan.FromMilliseconds(speechFrames * 20);
        if (first < 0)
        {
            return new SpeechAnalysis(speech, 0, 0, noiseFloor, peak);
        }

        var padding = SpeechAudio.SamplesIn(Padding);
        var start = Math.Max(0, (first * FrameSamples) - padding);
        var end = Math.Min(samples.Length, ((last + 1) * FrameSamples) + padding);
        return new SpeechAnalysis(speech, start, end - start, noiseFloor, peak);
    }
}
