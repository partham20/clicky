using Clicky.Core.Geometry;

namespace Clicky.Core.Ink;

/// <summary>Where the pointer was, in physical pixels, and when, measured from the start of the recording.</summary>
public readonly record struct InkSample(PixelPoint Position, TimeSpan Time);

/// <summary>
/// The pointer's path while the user held Talk, in physical pixels on the virtual desktop. A sample
/// is recorded only when the pointer moves, so a long gap between two samples means it rested.
/// </summary>
/// <param name="Samples">In time order.</param>
/// <param name="Duration">How long the recording ran; the pointer stayed at the last sample until then.</param>
public sealed record InkTrail(IReadOnlyList<InkSample> Samples, TimeSpan Duration)
{
    public static InkTrail Empty { get; } = new([], TimeSpan.Zero);

    public bool IsEmpty => Samples.Count == 0;
}

/// <summary>
/// Collects samples for an <see cref="InkTrail"/>. Not thread-safe: one writer, or a lock around it.
/// </summary>
public sealed class InkTrailBuilder
{
    /// <summary>About four minutes of constant movement at 125 Hz; the trail stops growing after that.</summary>
    public const int MaxSamples = 30_000;

    private readonly List<InkSample> _samples = [];

    public int Count => _samples.Count;

    public InkSample this[int index] => _samples[index];

    /// <summary>Adds a sample unless the pointer has not moved or the trail is full. Time must not go backwards.</summary>
    /// <returns>True if the sample was added.</returns>
    public bool Add(PixelPoint position, TimeSpan time)
    {
        if (_samples.Count >= MaxSamples)
        {
            return false;
        }

        if (_samples.Count > 0)
        {
            var last = _samples[^1];
            if (last.Position == position)
            {
                return false;
            }

            if (time < last.Time)
            {
                time = last.Time;
            }
        }

        _samples.Add(new InkSample(position, time));
        return true;
    }

    public InkTrail Build(TimeSpan duration)
    {
        var end = _samples.Count > 0 && _samples[^1].Time > duration ? _samples[^1].Time : duration;
        return new InkTrail([.. _samples], end);
    }
}
