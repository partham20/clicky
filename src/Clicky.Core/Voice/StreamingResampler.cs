namespace Clicky.Core.Voice;

/// <summary>
/// Band-limited sample-rate conversion for a stream that arrives in arbitrary chunks, e.g. a
/// 48 kHz mic down to the 16 kHz speech models want. Output does not depend on how the input was
/// chunked.
/// </summary>
/// <remarks>
/// <para>Blackman-windowed sinc, cut off at 90% of the lower Nyquist rate so nothing above
/// 7.2 kHz folds back into a 16 kHz result, interpolated from a precomputed table. Output sample n
/// sits exactly at input position n × inRate / outRate (rational arithmetic, no drift), so the
/// output is not delayed; the price is that each one waits for <see cref="HalfWidth"/> input
/// samples of look-ahead, which <see cref="Flush"/> releases at the end.</para>
/// <para>Costs about 80 multiply-adds per output sample for 48 kHz to 16 kHz.</para>
/// </remarks>
public sealed class StreamingResampler
{
    private const int ZeroCrossings = 12;
    private const int TablePhases = 256;
    private const double CutoffFraction = 0.9;

    private readonly int _inRate;
    private readonly int _outRate;
    private readonly float[] _kernel;

    // Pending input: _history[0] is absolute input sample _historyStart.
    private readonly SampleBuffer _history = new(4096);
    private long _historyStart;
    private long _inputCount;
    private long _produced;

    public StreamingResampler(int inRate, int outRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outRate, 1);
        _inRate = inRate;
        _outRate = outRate;

        // Cycles per input sample.
        var cutoff = 0.5 * Math.Min(1.0, (double)outRate / inRate) * CutoffFraction;
        HalfWidth = (int)Math.Ceiling(ZeroCrossings / (2 * cutoff));

        _kernel = new float[(HalfWidth * TablePhases) + 2];
        for (var i = 0; i < _kernel.Length; i++)
        {
            var distance = (double)i / TablePhases;
            _kernel[i] = (float)Kernel(distance, cutoff, HalfWidth);
        }
    }

    public int InputRate => _inRate;

    public int OutputRate => _outRate;

    /// <summary>Input samples of look-ahead each output sample needs (and of history it keeps).</summary>
    public int HalfWidth { get; }

    /// <summary>Feeds input and appends every output sample that is now fully determined.</summary>
    public void Process(ReadOnlySpan<float> input, SampleBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_inRate == _outRate)
        {
            output.Append(input);
            _inputCount += input.Length;
            _produced += input.Length;
            return;
        }

        _history.Append(input);
        _inputCount += input.Length;
        Produce(output, flushing: false);
    }

    /// <summary>
    /// Ends the stream: emits the remaining output, treating input past the end as silence, so the
    /// total output is ceil(input × outRate / inRate) samples. The resampler can then be reused.
    /// </summary>
    public void Flush(SampleBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_inRate != _outRate)
        {
            Produce(output, flushing: true);
        }

        _history.Clear();
        _historyStart = 0;
        _inputCount = 0;
        _produced = 0;
    }

    private static double Kernel(double distance, double cutoff, int halfWidth)
    {
        if (distance >= halfWidth)
        {
            return 0;
        }

        var x = 2 * cutoff * distance;
        var sinc = x == 0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
        var w = distance / halfWidth;
        var window = 0.42 + (0.5 * Math.Cos(Math.PI * w)) + (0.08 * Math.Cos(2 * Math.PI * w));
        return 2 * cutoff * sinc * window;
    }

    private void Produce(SampleBuffer output, bool flushing)
    {
        var history = _history.Span;
        var historyEnd = _historyStart + history.Length;
        while (true)
        {
            var numerator = _produced * _inRate;
            if (flushing)
            {
                // Output n exists while n < input × out / in.
                if (numerator >= _inputCount * _outRate)
                {
                    break;
                }
            }

            var center = numerator / _outRate;
            if (!flushing && center + HalfWidth >= historyEnd)
            {
                break;
            }

            var fraction = (double)(numerator % _outRate) / _outRate;
            var sum = 0.0;
            for (var k = center - HalfWidth + 1; k <= center + HalfWidth; k++)
            {
                if (k < _historyStart || k >= historyEnd)
                {
                    continue;
                }

                var position = Math.Abs(k - center - fraction) * TablePhases;
                var index = (int)position;
                var t = position - index;
                var weight = _kernel[index] + ((_kernel[index + 1] - _kernel[index]) * t);
                sum += history[(int)(k - _historyStart)] * weight;
            }

            output.Add((float)sum);
            _produced++;
        }

        // Keep only what the next output still reaches back to.
        var nextCenter = _produced * _inRate / _outRate;
        var keepFrom = Math.Max(_historyStart, nextCenter - HalfWidth + 1);
        var drop = (int)Math.Min(keepFrom - _historyStart, history.Length);
        if (drop > 0)
        {
            _history.RemoveFront(drop);
            _historyStart += drop;
        }
    }
}
