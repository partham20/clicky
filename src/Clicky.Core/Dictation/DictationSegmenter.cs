using Clicky.Core.Voice;

namespace Clicky.Core.Dictation;

/// <summary>A run of dictation audio cut at a pause, ready to transcribe.</summary>
/// <param name="Samples">16 kHz mono.</param>
/// <param name="Offset">Where it starts, from the beginning of the dictation.</param>
public sealed record DictationChunk(float[] Samples, TimeSpan Offset)
{
    public TimeSpan Duration => SpeechAudio.DurationOf(Samples.Length);
}

/// <summary>
/// Cuts a live 16 kHz stream into chunks at the pauses between sentences, so a long dictation is
/// transcribed while it is spoken: each chunk fits Whisper's 30 s window, memory stays flat
/// however long the session runs, and whatever was already transcribed survives a mic that dies.
/// </summary>
/// <remarks>
/// <para>The same energy test as <see cref="SpeechDetector"/>, but against a noise floor that
/// follows the room over the last <see cref="NoiseWindowFrames"/> frames.</para>
/// <para>Cut rules: after at least <see cref="MinSpeechFrames"/> of speech, a pause of
/// <see cref="ShortPauseFrames"/> ends a chunk that is <see cref="PreferredChunkFrames"/> long, and
/// a pause of <see cref="LongPauseFrames"/> ends any chunk. Each cut lands in the middle of the
/// pause. A chunk reaching <see cref="MaxChunkFrames"/> is cut at the quietest moment of its last
/// 10 s. Long silence with no speech is dropped as it goes, keeping the last second.</para>
/// <para>Not thread-safe: one writer (the capture thread).</para>
/// </remarks>
public sealed class DictationSegmenter
{
    public const int FrameSamples = SpeechDetector.FrameSamples;

    /// <summary>A chunk needs this much speech (frames of 20 ms) to be worth cutting at a pause.</summary>
    public const int MinSpeechFrames = 15;

    /// <summary>600 ms: the pause after a sentence.</summary>
    public const int ShortPauseFrames = 30;

    /// <summary>1.2 s: a pause that ends a chunk however short it is.</summary>
    public const int LongPauseFrames = 60;

    /// <summary>4 s: long enough to give Whisper context.</summary>
    public const int PreferredChunkFrames = 200;

    /// <summary>25 s, inside Whisper's 30 s window with room for padding.</summary>
    public const int MaxChunkFrames = 1250;

    /// <summary>When a chunk is forced, the cut is searched for in this many trailing frames (10 s).</summary>
    public const int ForcedCutSearchFrames = 500;

    /// <summary>Silence-only audio beyond this (10 s) is dropped as it arrives.</summary>
    public const int IdleDropFrames = 500;

    /// <summary>The noise floor follows the last 15 s.</summary>
    public const int NoiseWindowFrames = 750;

    private const int NoiseUpdateFrames = 25;
    private const double InitialNoiseFloorDb = -70;

    private readonly SampleBuffer _chunk = new(SpeechAudio.SampleRate * 30);
    private readonly List<double> _energies = new(MaxChunkFrames + 1);
    private readonly double[] _noise = new double[NoiseWindowFrames];
    private readonly float[] _frame = new float[FrameSamples];

    private int _frameFill;
    private int _noiseCount;
    private int _noiseNext;
    private int _sinceNoiseUpdate;
    private double _noiseFloor = InitialNoiseFloorDb;
    private int _speechFrames;
    private int _silentRun;
    private long _chunkStart;

    /// <summary>Estimated background level, dBFS.</summary>
    public double NoiseFloorDb => _noiseFloor;

    /// <summary>Samples waiting in the current, unfinished chunk.</summary>
    public int Pending => _chunk.Count + _frameFill;

    /// <summary>Adds audio; returns the chunks it completed, in order (usually none).</summary>
    public IReadOnlyList<DictationChunk> Append(ReadOnlySpan<float> samples)
    {
        List<DictationChunk>? done = null;
        while (samples.Length > 0)
        {
            var take = Math.Min(FrameSamples - _frameFill, samples.Length);
            samples[..take].CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += take;
            samples = samples[take..];
            if (_frameFill == FrameSamples)
            {
                _frameFill = 0;
                if (AddFrame() is { } chunk)
                {
                    (done ??= []).Add(chunk);
                }
            }
        }

        return done ?? (IReadOnlyList<DictationChunk>)[];
    }

    /// <summary>The stream ended: returns whatever is left as a last chunk, or null if nothing is.</summary>
    public DictationChunk? Flush()
    {
        _chunk.Append(_frame.AsSpan(0, _frameFill));
        _frameFill = 0;
        if (_chunk.Count == 0)
        {
            return null;
        }

        var chunk = new DictationChunk(_chunk.ToArray(), SpeechAudio.DurationOf((int)_chunkStart));
        _chunkStart += _chunk.Count;
        _chunk.Clear();
        _energies.Clear();
        _speechFrames = 0;
        _silentRun = 0;
        return chunk;
    }

    private DictationChunk? AddFrame()
    {
        var sum = 0.0;
        foreach (var sample in _frame)
        {
            sum += sample * sample;
        }

        var energy = 10 * Math.Log10(Math.Max(sum / FrameSamples, 1e-12));
        _chunk.Append(_frame);
        _energies.Add(energy);
        TrackNoise(energy);

        var threshold = Math.Clamp(_noiseFloor + SpeechDetector.MarginDb, SpeechDetector.AbsoluteFloorDb, SpeechDetector.CertainSpeechDb);
        if (energy >= threshold)
        {
            _speechFrames++;
            _silentRun = 0;
        }
        else
        {
            _silentRun++;
        }

        var frames = _energies.Count;
        if (_speechFrames >= MinSpeechFrames
            && ((_silentRun >= ShortPauseFrames && frames >= PreferredChunkFrames) || _silentRun >= LongPauseFrames))
        {
            return Cut(frames - (_silentRun / 2));
        }

        if (frames >= MaxChunkFrames)
        {
            return Cut(QuietestFrame());
        }

        if (_speechFrames == 0 && frames >= IdleDropFrames)
        {
            // Nobody is talking: keep the last second as lead-in and forget the rest.
            Drop(frames - (SpeechAudio.SampleRate / FrameSamples));
        }

        return null;
    }

    private void TrackNoise(double energy)
    {
        if (energy <= SpeechDetector.DigitalSilenceDb)
        {
            return;
        }

        _noise[_noiseNext] = energy;
        _noiseNext = (_noiseNext + 1) % _noise.Length;
        _noiseCount = Math.Min(_noiseCount + 1, _noise.Length);
        if (++_sinceNoiseUpdate < NoiseUpdateFrames && _noiseCount > NoiseUpdateFrames)
        {
            return;
        }

        _sinceNoiseUpdate = 0;
        var sorted = _noise.AsSpan(0, _noiseCount).ToArray();
        Array.Sort(sorted);
        _noiseFloor = sorted[(int)(sorted.Length * 0.1)];
    }

    /// <summary>Middle of the quietest 200 ms in the last <see cref="ForcedCutSearchFrames"/> frames.</summary>
    private int QuietestFrame()
    {
        const int Window = 10;
        var frames = _energies.Count;
        var from = Math.Max(Window, frames - ForcedCutSearchFrames);
        var best = frames;
        var bestEnergy = double.MaxValue;
        for (var end = from; end <= frames; end++)
        {
            var total = 0.0;
            for (var i = end - Window; i < end; i++)
            {
                total += _energies[i];
            }

            if (total < bestEnergy)
            {
                bestEnergy = total;
                best = end - (Window / 2);
            }
        }

        return best;
    }

    private DictationChunk Cut(int frame)
    {
        var samples = frame * FrameSamples;
        var chunk = new DictationChunk(_chunk.Span[..samples].ToArray(), SpeechAudio.DurationOf((int)_chunkStart));
        Drop(frame);
        return chunk;
    }

    /// <summary>Removes the first <paramref name="frames"/> frames from the current chunk.</summary>
    private void Drop(int frames)
    {
        _chunk.RemoveFront(frames * FrameSamples);
        _chunkStart += frames * FrameSamples;
        _energies.RemoveRange(0, frames);

        var threshold = Math.Clamp(_noiseFloor + SpeechDetector.MarginDb, SpeechDetector.AbsoluteFloorDb, SpeechDetector.CertainSpeechDb);
        _speechFrames = _energies.Count(e => e >= threshold);
        _silentRun = Math.Min(_silentRun, _energies.Count);
    }
}
