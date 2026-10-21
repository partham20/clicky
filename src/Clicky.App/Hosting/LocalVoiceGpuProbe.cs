using System.Diagnostics;
using Clicky.Core.Voice;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// Checks the GPU voice in a child copy of Clicky (Clicky.exe --probe-voice-gpu), so a CUDA runtime
/// that crashes or hangs takes down only the child.
/// </summary>
internal sealed class LocalVoiceGpuProbe(ILogger<LocalVoiceGpuProbe> logger) : ILocalVoiceGpuProbe
{
    /// <summary>The first CUDA start on a PC can compile kernels for a while; past this it counts as failed.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public async Task<LocalVoiceGpuCheck> CheckAsync(string runtimeFolder, string modelFolder, CancellationToken cancellationToken)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return new LocalVoiceGpuCheck(false, "Clicky could not find its own program file.");
        }

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(LocalVoiceGpu.ProbeSwitch);
        start.ArgumentList.Add(runtimeFolder);
        start.ArgumentList.Add(modelFolder);

        using var process = Process.Start(start);
        if (process is null)
        {
            return new LocalVoiceGpuCheck(false, "The check could not start.");
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new LocalVoiceGpuCheck(false, $"The check did not finish within {Timeout.TotalSeconds:0} s.");
        }

        var line = (await output.ConfigureAwait(false)).Trim().Split('\n').LastOrDefault()?.Trim() ?? string.Empty;
        var stderr = (await errors.ConfigureAwait(false)).Trim();
        if (stderr.Length > 0)
        {
            logger.LogInformation("GPU voice check output: {Errors}", stderr.Length <= 2000 ? stderr : stderr[^2000..]);
        }

        if (process.ExitCode == 0)
        {
            return new LocalVoiceGpuCheck(true, line);
        }

        var detail = line.Length > 0
            ? line
            : $"the check crashed (exit code 0x{process.ExitCode:X8}){(stderr.Length > 0 ? ": " + stderr.Split('\n')[^1].Trim() : string.Empty)}";
        return new LocalVoiceGpuCheck(false, detail);
    }
}
