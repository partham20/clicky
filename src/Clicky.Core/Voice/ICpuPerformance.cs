namespace Clicky.Core.Voice;

/// <summary>What the local voice needs to know about the CPU, and a way to ask it for full speed.</summary>
public interface ICpuPerformance
{
    /// <summary>
    /// Physical cores of the fastest kind: the P-cores of a hybrid Intel CPU (6 on an i7-13800H),
    /// otherwise every physical core.
    /// </summary>
    int PerformanceCores { get; }

    /// <summary>
    /// Runs this process at full speed until the result is disposed. Windows 11 otherwise treats a
    /// tray app as background work (EcoQoS): its threads go to efficiency cores at low clocks,
    /// which roughly doubles how long a sentence takes. Nested calls are counted.
    /// </summary>
    IDisposable Boost();
}

/// <summary>Without platform help: half the logical processors, and no boost.</summary>
public sealed class DefaultCpuPerformance : ICpuPerformance
{
    public static DefaultCpuPerformance Instance { get; } = new();

    public int PerformanceCores => Math.Max(1, Environment.ProcessorCount / 2);

    public IDisposable Boost() => NoBoost.Instance;

    private sealed class NoBoost : IDisposable
    {
        public static NoBoost Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
