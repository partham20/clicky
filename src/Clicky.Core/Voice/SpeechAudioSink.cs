namespace Clicky.Core.Voice;

/// <summary>Constants for the audio every speech engine here consumes.</summary>
public static class SpeechAudio
{
    /// <summary>Whisper and most cloud engines work on 16 kHz mono; anything more is wasted bandwidth.</summary>
    public const int SampleRate = 16_000;

    public static TimeSpan DurationOf(int samples) => TimeSpan.FromSeconds((double)samples / SampleRate);

    public static int SamplesIn(TimeSpan duration) => (int)Math.Round(duration.TotalSeconds * SampleRate);
}

/// <summary>
/// Takes a capture device's raw buffers and keeps them as 16 kHz mono float, with a live input
/// level for the buddy to show. One per recording.
/// </summary>
/// <remarks>
/// <see cref="Write"/> is called on the capture thread; <see cref="Complete"/> from anywhere once
/// capture has stopped. <see cref="Level"/> is safe to read from any thread.
/// </remarks>
public sealed class SpeechAudioSink
{
    /// <summary>Level is reported once per this much audio.</summary>
    public static readonly TimeSpan LevelInterval = TimeSpan.FromMilliseconds(50);

    // dBFS range mapped onto Level 0..1. Quiet room tone sits below the floor; normal speech into
    // a laptop mic array (which has its own AGC) lands around -30 to -20 dBFS.
    private const double LevelFloorDb = -55;
    private const double LevelCeilingDb = -15;

    private readonly Lock _gate = new();
    private readonly PcmFormat _format;
    private readonly StreamingResampler _resampler;
    private readonly SampleBuffer _speech;
    private readonly int _maxSamples;
    private readonly int _levelBlock;

    private float[] _mono = new float[4800];
    private double _levelSquares;
    private int _levelCount;
    private float _level;
    private bool _completed;

    public SpeechAudioSink(PcmFormat format, TimeSpan maxDuration)
    {
        format.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDuration, TimeSpan.Zero);
        _format = format;
        _resampler = new StreamingResampler(format.SampleRate, SpeechAudio.SampleRate);
        _maxSamples = SpeechAudio.SamplesIn(maxDuration);
        _speech = new SampleBuffer(SpeechAudio.SampleRate * 10);
        _levelBlock = Math.Max(1, (int)(format.SampleRate * LevelInterval.TotalSeconds));
    }

    /// <summary>Raised on the capture thread every <see cref="LevelInterval"/> of audio.</summary>
    public event Action<float>? LevelChanged;

    public PcmFormat Format => _format;

    /// <summary>Most recent input level, 0 (silence) to 1 (loud speech).</summary>
    public float Level => Volatile.Read(ref _level);

    /// <summary>True once the recording hit its maximum length; later audio was dropped.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Speech samples kept so far (16 kHz).</summary>
    public int SampleCount
    {
        get
        {
            lock (_gate)
            {
                return _speech.Count;
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        float[]? levels = null;
        lock (_gate)
        {
            if (_completed || Truncated)
            {
                return;
            }

            var frames = data.Length / _format.BlockAlign;
            if (frames == 0)
            {
                return;
            }

            if (_mono.Length < frames)
            {
                _mono = new float[frames];
            }

            var mono = _mono.AsSpan(0, PcmDecoder.ToMono(data, _format, _mono));
            levels = MeasureLevels(mono);

            _resampler.Process(mono, _speech);
            if (_speech.Count >= _maxSamples)
            {
                Truncated = true;
            }
        }

        if (levels is not null && LevelChanged is { } handler)
        {
            foreach (var level in levels)
            {
                handler(level);
            }
        }
    }

    /// <summary>Ends the recording and returns the speech audio (16 kHz mono). Idempotent.</summary>
    public float[] Complete()
    {
        lock (_gate)
        {
            if (!_completed)
            {
                _completed = true;
                if (!Truncated)
                {
                    _resampler.Flush(_speech);
                }
            }

            var count = Math.Min(_speech.Count, _maxSamples);
            return _speech.Span[..count].ToArray();
        }
    }

    /// <summary>Maps an RMS level in dBFS onto the 0..1 scale <see cref="Level"/> uses.</summary>
    public static float LevelFromDb(double db) => (float)Math.Clamp((db - LevelFloorDb) / (LevelCeilingDb - LevelFloorDb), 0, 1);

    private float[]? MeasureLevels(ReadOnlySpan<float> mono)
    {
        List<float>? levels = null;
        foreach (var sample in mono)
        {
            _levelSquares += sample * sample;
            if (++_levelCount < _levelBlock)
            {
                continue;
            }

            var rms = Math.Sqrt(_levelSquares / _levelCount);
            var level = LevelFromDb(20 * Math.Log10(Math.Max(rms, 1e-9)));
            Volatile.Write(ref _level, level);
            (levels ??= []).Add(level);
            _levelSquares = 0;
            _levelCount = 0;
        }

        return levels?.ToArray();
    }
}
