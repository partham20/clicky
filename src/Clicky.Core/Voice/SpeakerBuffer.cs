namespace Clicky.Core.Voice;

/// <summary>
/// The audio queue between the speech pipeline and the output device: the pipeline writes mono
/// samples at the device rate, the device's render thread pulls them. Plays silence when it runs
/// dry, fades out on <see cref="Stop"/> instead of clicking, meters the level for the buddy, and
/// tells waiters when playback reaches a position.
/// </summary>
/// <remarks>Thread-safe: one writer, one render thread, any number of waiters.</remarks>
public sealed class SpeakerBuffer
{
    private readonly Lock _gate = new();
    private readonly SampleBuffer _queue = new(48_000);
    private readonly List<(long Position, TaskCompletionSource Done)> _waiters = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _fadeSamples;
    private readonly int _levelWindow;

    private int _queueStart;
    private long _written;
    private long _played;
    private bool _completed;
    private int _fadeRemaining = -1;
    private float _gain;
    private double _levelSum;
    private int _levelCount;
    private long _underrunSamples;

    /// <param name="sampleRate">Device rate of the samples written.</param>
    /// <param name="gain">Linear volume 0..1.</param>
    /// <param name="fade">Length of the fade-out when stopped early.</param>
    public SpeakerBuffer(int sampleRate, float gain = 1f, TimeSpan? fade = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        SampleRate = sampleRate;
        Gain = gain;
        _fadeSamples = Math.Max(1, (int)(sampleRate * (fade ?? TimeSpan.FromMilliseconds(15)).TotalSeconds));
        _levelWindow = Math.Max(1, sampleRate / 20);
    }

    /// <summary>Speech level 0..1, about 20 times a second while playing. Raised on the render thread.</summary>
    public event Action<float>? LevelChanged;

    public int SampleRate { get; }

    public float Gain
    {
        get => Volatile.Read(ref _gain);
        set => Volatile.Write(ref _gain, float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f);
    }

    /// <summary>Samples written so far: the position the next write starts at.</summary>
    public long Written
    {
        get
        {
            lock (_gate)
            {
                return _written;
            }
        }
    }

    /// <summary>Samples handed to the device so far (silence while starved not counted).</summary>
    public long Played
    {
        get
        {
            lock (_gate)
            {
                return _played;
            }
        }
    }

    /// <summary>Time the device spent waiting for audio after playback had started, summed.</summary>
    public TimeSpan Underruns
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromSeconds((double)_underrunSamples / SampleRate);
            }
        }
    }

    /// <summary>Completes once everything written has played after <see cref="Complete"/>, or the fade after <see cref="Stop"/> has finished.</summary>
    public Task Drained => _drained.Task;

    public bool IsDrained => _drained.Task.IsCompleted;

    /// <summary>Queues samples. Ignored after <see cref="Complete"/> or <see cref="Stop"/>.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (_completed || _fadeRemaining >= 0)
            {
                return;
            }

            Compact();
            _queue.Append(samples);
            _written += samples.Length;
        }
    }

    /// <summary>Appends silence, e.g. the pause between two sentences.</summary>
    public void WriteSilence(TimeSpan duration)
    {
        var count = (int)Math.Round(duration.TotalSeconds * SampleRate);
        if (count > 0)
        {
            Write(new float[count]);
        }
    }

    /// <summary>Nothing more will be written; <see cref="Drained"/> completes when the queue empties.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
        }
    }

    /// <summary>Barge-in: fades out what is playing over a few ms and drops the rest.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_fadeRemaining < 0 && !_drained.Task.IsCompleted)
            {
                _fadeRemaining = Math.Min(_fadeSamples, _queue.Count - _queueStart);
                _completed = true;
            }
        }
    }

    /// <summary>Completes when playback has reached <paramref name="position"/> (or the buffer drained or stopped first).</summary>
    public Task WhenPlayed(long position)
    {
        lock (_gate)
        {
            if (_played >= position || _drained.Task.IsCompleted)
            {
                return Task.CompletedTask;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((position, done));
            return done.Task;
        }
    }

    /// <summary>
    /// Render thread: fills <paramref name="output"/> with the next samples (silence once dry).
    /// Returns false once drained, when the device can be stopped.
    /// </summary>
    public bool Render(Span<float> output)
    {
        List<TaskCompletionSource>? reached = null;
        bool drainedNow = false;
        float? level = null;
        var gain = Gain;

        lock (_gate)
        {
            var available = _queue.Count - _queueStart;
            var count = Math.Min(available, output.Length);
            if (_fadeRemaining >= 0)
            {
                count = Math.Min(count, _fadeRemaining);
            }

            var source = _queue.Span.Slice(_queueStart, count);
            for (var i = 0; i < count; i++)
            {
                var g = gain;
                if (_fadeRemaining >= 0)
                {
                    g *= (float)(_fadeRemaining - i) / _fadeSamples;
                }

                var sample = source[i] * g;
                output[i] = sample;
                _levelSum += sample * sample;
            }

            output[count..].Clear();
            _queueStart += count;
            _played += count;
            _levelCount += output.Length;

            if (_fadeRemaining >= 0)
            {
                _fadeRemaining -= count;
                if (_fadeRemaining <= 0)
                {
                    _queue.Clear();
                    _queueStart = 0;
                    drainedNow = true;
                }
            }
            else if (count < output.Length)
            {
                if (_completed)
                {
                    drainedNow = true;
                }
                else if (_played > 0)
                {
                    _underrunSamples += output.Length - count;
                }
            }

            if (_levelCount >= _levelWindow || drainedNow)
            {
                // Speech RMS rarely exceeds 0.3; scale so normal speech fills most of the range.
                var rms = Math.Sqrt(_levelSum / Math.Max(1, _levelCount));
                level = drainedNow ? 0f : (float)Math.Clamp(rms * 4, 0, 1);
                _levelSum = 0;
                _levelCount = 0;
            }

            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Position <= _played || drainedNow)
                {
                    (reached ??= []).Add(_waiters[i].Done);
                    _waiters.RemoveAt(i);
                }
            }
        }

        if (level is { } l)
        {
            LevelChanged?.Invoke(l);
        }

        if (reached is not null)
        {
            foreach (var done in reached)
            {
                done.TrySetResult();
            }
        }

        if (drainedNow)
        {
            _drained.TrySetResult();
        }

        return !_drained.Task.IsCompleted;
    }

    /// <summary>The device failed: release everyone waiting.</summary>
    public void Abort()
    {
        List<TaskCompletionSource> waiters;
        lock (_gate)
        {
            _completed = true;
            _queue.Clear();
            _queueStart = 0;
            waiters = [.. _waiters.Select(w => w.Done)];
            _waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult();
        }

        _drained.TrySetResult();
    }

    // Drop played samples once they are the bulk of the queue, so writes don't copy the whole history.
    private void Compact()
    {
        if (_queueStart > 0 && _queueStart >= _queue.Count / 2)
        {
            _queue.RemoveFront(_queueStart);
            _queueStart = 0;
        }
    }
}
