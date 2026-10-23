using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SherpaOnnx;

namespace Clicky.Core.Voice;

/// <summary>What a GPU check found.</summary>
/// <param name="Works">The voice ran on the GPU.</param>
/// <param name="Detail">One line for Settings and the log: the time it took, or what is missing.</param>
public sealed record LocalVoiceGpuCheck(bool Works, string Detail);

/// <summary>Runs <see cref="LocalVoiceGpu.Probe"/> somewhere a native crash can't take Clicky down with it.</summary>
public interface ILocalVoiceGpuProbe
{
    Task<LocalVoiceGpuCheck> CheckAsync(string runtimeFolder, string modelFolder, CancellationToken cancellationToken);
}

/// <summary>
/// The NVIDIA (CUDA) build of sherpa-onnx for the local voice: which native library this process
/// loads, and the check that it really works on this PC.
/// </summary>
/// <remarks>
/// <para>The NuGet package is CPU only (its DirectML support is compiled out). The CUDA build is a
/// separate download (<see cref="LocalVoiceModels.CudaRuntime"/>) and needs the CUDA 12 runtime and
/// cuDNN 9 from NVIDIA. A missing piece can make ONNX Runtime abort the process rather than fall
/// back, so the build is first tried in a child process (Clicky.exe --probe-voice-gpu) and only
/// loaded here once that passed.</para>
/// <para>The native library is chosen once per process, before the first call into sherpa-onnx;
/// switching between CPU and GPU applies on the next start.</para>
/// </remarks>
public static partial class LocalVoiceGpu
{
    /// <summary>Command-line switch for the child process that runs <see cref="Probe"/>.</summary>
    public const string ProbeSwitch = "--probe-voice-gpu";

    /// <summary>File in the runtime folder recording the last check: "ok" or "failed", the detail, then <see cref="CheckVersion"/>.</summary>
    public const string CheckFile = "gpu-check.txt";

    // Raise when the check changes: a result from an older check is then not trusted.
    private const string CheckVersion = "check 3";

    private const string NativeLibraryName = "sherpa-onnx-c-api";

    private static readonly Lock Gate = new();
    private static string? s_folder;
    private static bool s_resolverSet;

    /// <summary>The DLLs taken from the CUDA build's lib folder.</summary>
    public static IReadOnlyList<string> RuntimeFiles { get; } =
        ["sherpa-onnx-c-api.dll", "onnxruntime.dll", "onnxruntime_providers_cuda.dll", "onnxruntime_providers_shared.dll"];

    /// <summary>The GPU runtime folder this process loads sherpa-onnx from; null on the CPU build.</summary>
    public static string? Folder
    {
        get
        {
            lock (Gate)
            {
                return s_folder;
            }
        }
    }

    /// <summary>
    /// Makes this process load sherpa-onnx from <paramref name="folder"/>. Must run before the first
    /// call into sherpa-onnx; returns false (and changes nothing) once the CPU build is in use.
    /// </summary>
    public static bool UseFolder(string folder, bool cpuAlreadyLoaded)
    {
        lock (Gate)
        {
            if (s_folder is not null)
            {
                return string.Equals(s_folder, folder, StringComparison.OrdinalIgnoreCase);
            }

            if (cpuAlreadyLoaded || s_resolverSet)
            {
                return false;
            }

            AddNvidiaFoldersToPath();
            NativeLibrary.SetDllImportResolver(typeof(OfflineTts).Assembly, Resolve);
            s_resolverSet = true;
            s_folder = folder;
            return true;
        }
    }

    /// <summary>
    /// The last check recorded in <paramref name="runtimeFolder"/>, if any. One recorded by an older
    /// check doesn't count, so an update that changes the check runs it again.
    /// </summary>
    public static LocalVoiceGpuCheck? ReadCheck(string runtimeFolder)
    {
        try
        {
            var lines = File.ReadAllLines(Path.Combine(runtimeFolder, CheckFile));
            return lines is [var result, var detail, CheckVersion, ..] ? new LocalVoiceGpuCheck(result == "ok", detail) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void WriteCheck(string runtimeFolder, LocalVoiceGpuCheck check) =>
        File.WriteAllLines(Path.Combine(runtimeFolder, CheckFile), [check.Works ? "ok" : "failed", check.Detail.ReplaceLineEndings(" "), CheckVersion]);

    public static void ForgetCheck(string runtimeFolder)
    {
        try
        {
            File.Delete(Path.Combine(runtimeFolder, CheckFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The check itself, run in the child process: check the NVIDIA pieces (see
    /// <see cref="LocalVoiceGpuPreflight"/>), then load the CUDA build, speak a sentence on the GPU and
    /// confirm ONNX Runtime really loaded its CUDA provider rather than falling back to the CPU.
    /// Writes one line to <paramref name="output"/> and what it found (GPU, versions) to
    /// <paramref name="log"/>; exit code 0 if it works.
    /// </summary>
    public static int Probe(string runtimeFolder, string modelFolder, TextWriter output, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            return RunProbe(runtimeFolder, modelFolder, output, log);
        }
        catch (Exception ex)
        {
            output.WriteLine($"The check failed: {ex.GetType().Name}: {ex.Message}".ReplaceLineEndings(" "));
            return 3;
        }
    }

    private static int RunProbe(string runtimeFolder, string modelFolder, TextWriter output, TextWriter log)
    {
        if (!UseFolder(runtimeFolder, cpuAlreadyLoaded: false))
        {
            output.WriteLine("Could not select the GPU runtime.");
            return 3;
        }

        var problem = LocalVoiceGpuPreflight.FindProblem(runtimeFolder, out var found);
        if (found.Length > 0)
        {
            log.WriteLine(FoundPrefix + found);
        }

        if (problem is not null)
        {
            output.WriteLine(problem);
            return 2;
        }

        var kind = LocalTextToSpeech.DetectKind(modelFolder);
        if (kind is null)
        {
            output.WriteLine($"No voice model in {modelFolder}.");
            return 3;
        }

        var config = LocalTextToSpeech.ConfigFor(modelFolder, kind.Value, LocalVoiceModels.DefaultVoice, 1, "cuda");
        var stopwatch = Stopwatch.StartNew();
        using var tts = new OfflineTts(config);
        if (typeof(OfflineTts).GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(tts) is HandleRef { Handle: 0 })
        {
            output.WriteLine("The GPU runtime could not load the voice model.");
            return 3;
        }

        var load = stopwatch.Elapsed;
        tts.Generate("Checking the graphics card.", 1f, 0).Dispose();
        stopwatch.Restart();
        var audio = tts.Generate("This sentence is spoken by the graphics card.", 1f, 0);
        var seconds = audio.NumSamples / (double)Math.Max(1, audio.SampleRate);
        audio.Dispose();
        var synth = stopwatch.Elapsed;

        var onGpu = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Any(m => string.Equals(m.ModuleName, "onnxruntime_providers_cuda.dll", StringComparison.OrdinalIgnoreCase));
        if (!onGpu || seconds <= 0)
        {
            output.WriteLine("ONNX Runtime fell back to the CPU; the GPU is not usable for the voice.");
            return 2;
        }

        output.WriteLine($"Works: {synth.TotalMilliseconds:0} ms for {seconds:0.0} s of speech (loaded in {load.TotalSeconds:0.0} s).");
        return 0;
    }

    /// <summary>
    /// One line for a check that ended without saying why (ONNX Runtime aborted it): what the exit
    /// code means, and the last error ONNX Runtime or sherpa-onnx wrote to standard error.
    /// </summary>
    public static string DescribeCrash(int exitCode, string errorOutput)
    {
        var code = unchecked((uint)exitCode);
        var what = code switch
        {
            // abort(), which is where an exception nobody catches in native code ends up.
            0xC0000409 => "the GPU runtime stopped the check",
            0xC0000005 => "the GPU runtime crashed",
            0xE0434352 => "the check crashed",
            _ => "the check stopped",
        };
        var detail = $"{what} (exit code 0x{code:X8})";
        return LastError(errorOutput) is { } error ? $"{detail}: {error}" : detail;
    }

    /// <summary>Native standard error without the terminal colour codes ONNX Runtime puts in it.</summary>
    public static string WithoutColours(string text) => AnsiCode().Replace(text ?? string.Empty, string.Empty);

    /// <summary>The last error in a native library's standard error, without colour codes, log prefixes or source locations.</summary>
    internal static string? LastError(string errorOutput)
    {
        var lines = WithoutColours(errorOutput)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith(FoundPrefix, StringComparison.Ordinal) && l.Any(char.IsLetter))
            .ToList();
        var line = lines.LastOrDefault(l => l.Contains("[E:onnxruntime", StringComparison.Ordinal) || l.Contains("[F:onnxruntime", StringComparison.Ordinal))
            ?? lines.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("fail", StringComparison.OrdinalIgnoreCase))
            ?? lines.LastOrDefault();
        if (line is null)
        {
            return null;
        }

        line = OrtNoise().Replace(line, string.Empty);
        line = DllPath().Replace(line, "\"$1\"");
        line = Spaces().Replace(line, " ").Trim();
        return line.Length <= 400 ? line : line[..399] + "…";
    }

    // The start of the line Probe writes to standard error about what it found.
    private const string FoundPrefix = "Found: ";

    // Terminal colour codes: ONNX Runtime colours its log and ends each entry with ESC[m.
    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiCode();

    // "2026-10-23 03:20:11.6 [E:onnxruntime:Default, provider_bridge_ort.cc:2281 onnxruntime::TryGetProviderInfo_CUDA] ",
    // "D:\a\_work\1\s\onnxruntime\core\session\provider_bridge_ort.cc:1778 onnxruntime::ProviderLibrary::Get "
    // and "[ONNXRuntimeError] : 1 : FAIL : ".
    [GeneratedRegex(@"^.*?\[[A-Z]:onnxruntime:[^\]]*\]\s*|(?:[A-Za-z]:)?[\\/][^\s""]*\.(?:cc|cpp|h):\d+\s+\S+\s*|\[ONNXRuntimeError\]\s*:\s*\d+\s*:\s*\w+\s*:\s*")]
    private static partial Regex OrtNoise();

    // A quoted full path to a DLL: keep its file name.
    [GeneratedRegex(@"""(?:[A-Za-z]:)?[\\/][^""]*[\\/]([^""\\/]+)""")]
    private static partial Regex DllPath();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var folder = Folder;
        return folder is not null && string.Equals(name, NativeLibraryName, StringComparison.Ordinal)

            // A full path: Windows then finds onnxruntime.dll and the CUDA provider next to it.
            ? NativeLibrary.Load(Path.Combine(folder, NativeLibraryName + ".dll"))
            : IntPtr.Zero;
    }

    /// <summary>
    /// The CUDA and cuDNN installers don't always put their DLLs on PATH (cuDNN 9 never does), and
    /// the CUDA provider finds them through it; add the usual install folders for this process.
    /// </summary>
    private static void AddNvidiaFoldersToPath()
    {
        var folders = new List<string>();
        if (Environment.GetEnvironmentVariable("CUDA_PATH") is { Length: > 0 } cuda)
        {
            folders.Add(Path.Combine(cuda, "bin"));
            folders.Add(Path.Combine(cuda, "bin", "x64"));
        }

        var cudnnRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA", "CUDNN");
        if (Directory.Exists(cudnnRoot))
        {
            // C:\Program Files\NVIDIA\CUDNN\v9.x\bin\12.x, newest first.
            foreach (var version in Directory.EnumerateDirectories(cudnnRoot, "v9*").OrderDescending(StringComparer.OrdinalIgnoreCase))
            {
                var bin = Path.Combine(version, "bin");
                if (Directory.Exists(bin))
                {
                    folders.AddRange(Directory.EnumerateDirectories(bin, "12*").OrderDescending(StringComparer.OrdinalIgnoreCase));
                    folders.Add(bin);
                }
            }
        }

        var existing = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var add = folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => !existing.Contains(f, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (add.Count > 0)
        {
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, add.Append(existing)));
        }
    }
}
