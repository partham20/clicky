namespace Clicky.Platform.Windows.Display;

/// <summary>
/// One graphics-driver start-up at a time, process-wide: creating the capture D3D11 device, loading
/// whisper.cpp's Vulkan runtime and creating its GPU device.
/// </summary>
/// <remarks>
/// On Optimus laptops Clicky crashed in about one start in six with an access violation inside
/// D3D11CreateDevice while whisper.cpp was bringing up Vulkan on the same NVIDIA driver. An access
/// violation in native code ends the process (.NET cannot catch it), so the fix is to never let the
/// two overlap. Not re-entrant: never call into the other GPU user while holding it.
/// </remarks>
public static class GpuInitGate
{
    private static readonly SemaphoreSlim s_gate = new(1, 1);

    /// <summary>Blocks until no other GPU start-up is running; dispose the result to let the next one in.</summary>
    public static IDisposable Enter()
    {
        s_gate.Wait();
        return new Release();
    }

    private sealed class Release : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                s_gate.Release();
            }
        }
    }
}
