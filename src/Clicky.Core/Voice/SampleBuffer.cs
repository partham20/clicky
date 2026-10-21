namespace Clicky.Core.Voice;

/// <summary>A growable run of float samples. Not thread-safe.</summary>
public sealed class SampleBuffer
{
    private float[] _samples;

    public SampleBuffer(int initialCapacity = 16_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _samples = new float[Math.Max(initialCapacity, 16)];
    }

    public int Count { get; private set; }

    public ReadOnlySpan<float> Span => _samples.AsSpan(0, Count);

    public void Add(float sample)
    {
        EnsureCapacity(Count + 1);
        _samples[Count++] = sample;
    }

    public void Append(ReadOnlySpan<float> samples)
    {
        EnsureCapacity(Count + samples.Length);
        samples.CopyTo(_samples.AsSpan(Count));
        Count += samples.Length;
    }

    /// <summary>Drops the first <paramref name="count"/> samples.</summary>
    public void RemoveFront(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Count);
        _samples.AsSpan(count, Count - count).CopyTo(_samples);
        Count -= count;
    }

    public void Clear() => Count = 0;

    public float[] ToArray() => Span.ToArray();

    private void EnsureCapacity(int needed)
    {
        if (needed > _samples.Length)
        {
            Array.Resize(ref _samples, Math.Max(needed, _samples.Length * 2));
        }
    }
}
