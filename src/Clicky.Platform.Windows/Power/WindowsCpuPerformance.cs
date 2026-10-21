using System.Runtime.InteropServices;
using Clicky.Core.Voice;
using Microsoft.Extensions.Logging;

namespace Clicky.Platform.Windows.Power;

/// <summary>
/// CPU topology from GetLogicalProcessorInformationEx (P-cores are the cores of the highest
/// efficiency class), and EcoQoS opt-out through SetProcessInformation(ProcessPowerThrottling).
/// </summary>
/// <remarks>
/// The opt-out is only held while the local voice works, so the 125 Hz pointer polling and the rest
/// of an idle tray app keep the power-saving treatment Windows gives background processes.
/// </remarks>
public sealed class WindowsCpuPerformance : ICpuPerformance
{
    private readonly ILogger<WindowsCpuPerformance> _logger;
    private readonly Lock _gate = new();
    private readonly Lazy<int> _performanceCores;
    private int _boosts;
    private bool _warned;

    public WindowsCpuPerformance(ILogger<WindowsCpuPerformance> logger)
    {
        _logger = logger;
        _performanceCores = new Lazy<int>(CountPerformanceCores);
    }

    public int PerformanceCores => _performanceCores.Value;

    public IDisposable Boost()
    {
        lock (_gate)
        {
            if (_boosts++ == 0)
            {
                SetThrottling(throttled: false);
            }
        }

        return new Release(this);
    }

    /// <summary>Counts physical cores per efficiency class from the raw SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX buffer.</summary>
    internal static int CountPerformanceCores(ReadOnlySpan<byte> buffer)
    {
        // Each record: Relationship (4), Size (4), then PROCESSOR_RELATIONSHIP: Flags (1), EfficiencyClass (1), ...
        var counts = new Dictionary<byte, int>();
        var offset = 0;
        while (offset + 10 <= buffer.Length)
        {
            var relationship = BitConverter.ToInt32(buffer[offset..]);
            var size = BitConverter.ToInt32(buffer[(offset + 4)..]);
            if (size <= 0)
            {
                break;
            }

            if (relationship == NativeMethods.RelationProcessorCore)
            {
                var efficiency = buffer[offset + 9];
                counts[efficiency] = counts.GetValueOrDefault(efficiency) + 1;
            }

            offset += size;
        }

        return counts.Count == 0 ? 0 : counts[counts.Keys.Max()];
    }

    private int CountPerformanceCores()
    {
        try
        {
            uint length = 0;
            byte[] buffer;
            unsafe
            {
                // The first call fails with ERROR_INSUFFICIENT_BUFFER and reports the size needed.
                _ = NativeMethods.GetLogicalProcessorInformationEx(NativeMethods.RelationProcessorCore, null, ref length);
                buffer = new byte[length];
                fixed (byte* pointer = buffer)
                {
                    if (!NativeMethods.GetLogicalProcessorInformationEx(NativeMethods.RelationProcessorCore, pointer, ref length))
                    {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                    }
                }
            }

            var cores = CountPerformanceCores(buffer);
            _logger.LogInformation("CPU: {Cores} performance cores, {Logical} logical processors", cores, Environment.ProcessorCount);
            return cores > 0 ? cores : DefaultCpuPerformance.Instance.PerformanceCores;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "Could not read the CPU topology");
            return DefaultCpuPerformance.Instance.PerformanceCores;
        }
    }

    private void Unboost()
    {
        lock (_gate)
        {
            if (--_boosts == 0)
            {
                SetThrottling(throttled: true);
            }
        }
    }

    // Under _gate.
    private void SetThrottling(bool throttled)
    {
        var state = new NativeMethods.ProcessPowerThrottlingState
        {
            Version = NativeMethods.ProcessPowerThrottlingCurrentVersion,
            ControlMask = NativeMethods.ProcessPowerThrottlingExecutionSpeed,

            // Back to "let Windows decide" rather than forcing EcoQoS on.
            StateMask = 0,
        };
        if (throttled)
        {
            state.ControlMask = 0;
        }

        if (!NativeMethods.SetProcessInformation(NativeMethods.GetCurrentProcess(), NativeMethods.ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<NativeMethods.ProcessPowerThrottlingState>())
            && !_warned)
        {
            _warned = true;
            _logger.LogWarning("Could not change power throttling (error {Error}); the local voice may run on efficiency cores", Marshal.GetLastPInvokeError());
        }
    }

    private sealed class Release(WindowsCpuPerformance owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Unboost();
            }
        }
    }
}
