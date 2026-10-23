using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace Clicky.Core.Voice;

/// <summary>
/// The part of the GPU check that runs before sherpa-onnx touches CUDA: finds and loads the NVIDIA
/// libraries, reads their versions and the GPU, and loads the CUDA provider itself.
/// </summary>
/// <remarks>
/// sherpa-onnx doesn't catch the exception ONNX Runtime throws when its CUDA provider can't load or
/// start, so any such failure aborts the process (exit code 0xC0000409) with the reason only on
/// standard error. Finding it here first turns it into a sentence. The libraries stay loaded, so
/// ONNX Runtime later gets the same copies this check looked at.
/// </remarks>
internal static class LocalVoiceGpuPreflight
{
    /// <summary>
    /// GPU code in the CUDA build (from its fatbins): machine code for these compute capabilities,
    /// which also runs on later minors of the same major, plus PTX for 12.0 that the driver compiles
    /// for anything newer.
    /// </summary>
    private static readonly (int Major, int Minor)[] Kernels = [(6, 1), (7, 5), (8, 6), (8, 9), (12, 0)];

    private static readonly (int Major, int Minor) Ptx = (12, 0);

    private const string ProviderFile = "onnxruntime_providers_cuda.dll";

    // cuDNN 9 loads these from its own folder the first time a model needs them.
    private static readonly string[] CudnnParts =
        ["cudnn_graph64_9.dll", "cudnn_ops64_9.dll", "cudnn_cnn64_9.dll", "cudnn_heuristic64_9.dll", "cudnn_engines_precompiled64_9.dll"];

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int IntResult(out int value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DeviceAttribute(out int value, int attribute, int device);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DeviceProperties([Out] byte[] properties, int device);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr ErrorText(int error);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nuint SizeResult();

    /// <summary>
    /// Returns what stops the GPU voice on this PC, or null if nothing does. <paramref name="found"/>
    /// describes what was found (GPU, driver, library versions) for the log.
    /// </summary>
    public static string? FindProblem(string runtimeFolder, out string found)
    {
        var notes = new List<string>();
        var problem = Check(runtimeFolder, notes);
        found = string.Join("; ", notes);
        return problem;
    }

    private static string? Check(string runtimeFolder, List<string> notes)
    {
        var missing = new List<string>();
        var cudart = LoadKept("cudart64_12.dll", "the CUDA 12 runtime", missing);
        LoadKept("cublasLt64_12.dll", "cuBLAS from CUDA 12", missing);
        LoadKept("cublas64_12.dll", "cuBLAS from CUDA 12", missing);
        var cudnn = LoadKept("cudnn64_9.dll", "cuDNN 9 for CUDA 12", missing);
        if (missing.Count > 0)
        {
            return $"Not found on this PC: {string.Join(", ", missing.Distinct())}. Install them from NVIDIA, then try again.";
        }

        if (Read(Function<IntResult>(cudart, "cudaRuntimeGetVersion")) is { } runtime)
        {
            notes.Add($"CUDA runtime {CudaVersion(runtime)} ({PathOf("cudart64_12.dll")})");
        }

        if (Read(Function<IntResult>(cudart, "cudaDriverGetVersion")) is { } driver)
        {
            notes.Add($"driver supports CUDA {CudaVersion(driver)}");
            if (driver is > 0 and < 12000)
            {
                return $"The NVIDIA driver only supports CUDA {CudaVersion(driver)}; the GPU voice needs CUDA 12. Update the NVIDIA driver, then try again.";
            }
        }

        if (Function<SizeResult>(cudnn, "cudnnGetVersion")?.Invoke() is { } cudnnVersion and > 0)
        {
            notes.Add($"cuDNN {CudnnVersion(cudnnVersion)} ({PathOf("cudnn64_9.dll")})");
        }

        if (CudnnProblem() is { } cudnnProblem)
        {
            return cudnnProblem;
        }

        if (GpuProblem(cudart, notes) is { } gpuProblem)
        {
            return gpuProblem;
        }

        // Last, with every NVIDIA piece in place: what the CUDA provider imports, which names the
        // exact function an older CUDA or Visual C++ runtime lacks. Only read, never loaded here:
        // its start-up code needs the host ONNX Runtime hands it first, and without that Windows
        // fails the load with error 1114 even when everything is in place.
        foreach (var file in new[] { "onnxruntime.dll", ProviderFile })
        {
            if (LoadProblem(Path.Combine(runtimeFolder, file)) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>Loads a library by name and keeps it loaded; null (and a note in <paramref name="missing"/>) if it can't be.</summary>
    private static IntPtr LoadKept(string library, string what, List<string> missing)
    {
        if (NativeLibrary.TryLoad(library, out var handle))
        {
            return handle;
        }

        missing.Add($"{what} ({library})");
        return IntPtr.Zero;
    }

    private static string? CudnnProblem()
    {
        var folder = Path.GetDirectoryName(PathOf("cudnn64_9.dll"));
        if (folder is null)
        {
            return null;
        }

        var absent = CudnnParts.Where(p => !File.Exists(Path.Combine(folder, p))).ToList();
        return absent.Count == 0
            ? null
            : $"cuDNN in {folder} is incomplete ({string.Join(", ", absent)} not there). Reinstall cuDNN 9 for CUDA 12, then try again.";
    }

    private static string? GpuProblem(IntPtr cudart, List<string> notes)
    {
        var count = Function<IntResult>(cudart, "cudaGetDeviceCount");
        if (count is null)
        {
            return null;
        }

        var error = count(out var devices);
        if (error != 0 || devices == 0)
        {
            var text = error == 0 ? null : Function<ErrorText>(cudart, "cudaGetErrorString")?.Invoke(error) is { } p and not 0 ? Marshal.PtrToStringAnsi(p) : null;
            return error switch
            {
                0 or 100 => "No NVIDIA GPU is available to CUDA. Check that the NVIDIA GPU is turned on in Device Manager and its driver is installed.",
                35 => "The NVIDIA driver is too old for this CUDA runtime. Update the NVIDIA driver, then try again.",
                _ => $"CUDA could not start the GPU (error {error}{(text is null ? string.Empty : ": " + text)}).",
            };
        }

        var name = "the NVIDIA GPU";
        var properties = new byte[4096];

        // cudaDeviceProp starts with char name[256].
        if (Function<DeviceProperties>(cudart, "cudaGetDeviceProperties_v2")?.Invoke(properties, 0) == 0)
        {
            var end = Array.IndexOf(properties, (byte)0, 0, 256);
            name = Encoding.UTF8.GetString(properties, 0, end < 0 ? 256 : end).Trim();
        }

        var attribute = Function<DeviceAttribute>(cudart, "cudaDeviceGetAttribute");
        if (attribute is null || attribute(out var major, 75, 0) != 0 || attribute(out var minor, 76, 0) != 0)
        {
            notes.Add(name);
            return null;
        }

        notes.Add($"{name} (compute {major}.{minor})");
        return Runs(major, minor)
            ? null
            : $"{name} (compute capability {major}.{minor}) is not supported by the GPU voice runtime, which has code for GeForce GTX 10 and 16, RTX 20, 30, 40 and 50 class GPUs.";
    }

    /// <summary>The CUDA build has GPU code that runs on this compute capability.</summary>
    internal static bool Runs(int major, int minor) =>
        Kernels.Any(k => k.Major == major && k.Minor <= minor) || (major, minor).CompareTo(Ptx) >= 0;

    /// <summary>
    /// Why <paramref name="path"/> wouldn't load, if a library it needs (directly or through another
    /// non-Windows library) is missing or too old to have a function it uses; null if none is.
    /// </summary>
    private static string? LoadProblem(string path)
    {
        var file = Path.GetFileName(path);
        try
        {
            return MissingPiece(path, file, new HashSet<string>(StringComparer.OrdinalIgnoreCase), depth: 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return $"{file} could not be read ({ex.Message}). Delete the GPU runtime folder so Clicky downloads it again.";
        }
    }

    private static string? MissingPiece(string path, string file, HashSet<string> seen, int depth)
    {
        foreach (var (library, functions) in ReadImports(path))
        {
            if (library.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) || library.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!NativeLibrary.TryLoad(library, out var handle)
                && !NativeLibrary.TryLoad(Path.Combine(Path.GetDirectoryName(path)!, library), out handle))
            {
                return $"{file} needs {library}, which could not be loaded. {Fix(library)}";
            }

            var where = PathOf(library);
            var absent = functions.FirstOrDefault(f => !NativeLibrary.TryGetExport(handle, f, out _));
            if (absent is not null)
            {
                var version = where is null ? null : FileVersionInfo.GetVersionInfo(where).FileVersion;
                return $"{library}{(where is null ? string.Empty : " in " + Path.GetDirectoryName(where))}{(string.IsNullOrWhiteSpace(version) ? string.Empty : $" (version {version.Trim()})")} is too old: it has no {absent}. {Fix(library)}";
            }

            // What the NVIDIA and runtime libraries need in turn; Windows' own are left alone.
            if (depth < 3 && where is not null && seen.Add(where)
                && !where.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                && MissingPiece(where, library, seen, depth + 1) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private static string Fix(string library) =>
        library.StartsWith("cudnn", StringComparison.OrdinalIgnoreCase) ? "Install the latest cuDNN 9 for CUDA 12 from NVIDIA, then try again."
        : library.StartsWith("cu", StringComparison.OrdinalIgnoreCase) || library.StartsWith("nv", StringComparison.OrdinalIgnoreCase)
            ? "Install the latest CUDA 12 toolkit (12.8 or newer) from NVIDIA, then try again."
        : library.StartsWith("msvcp", StringComparison.OrdinalIgnoreCase) || library.StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase)
            ? "Install the latest Microsoft Visual C++ Redistributable (x64), then try again."
        : "Reinstall the NVIDIA driver, then try again.";

    /// <summary>The libraries a DLL imports and the functions it imports from each, by name.</summary>
    internal static List<(string Library, List<string> Functions)> ReadImports(string path)
    {
        using var stream = File.OpenRead(path);
        var headers = new PEHeaders(stream);
        var pe = headers.PEHeader ?? throw new BadImageFormatException("No PE header.");
        var wide = pe.Magic == PEMagic.PE32Plus;
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        long Seek(int rva)
        {
            foreach (var section in headers.SectionHeaders)
            {
                if (rva >= section.VirtualAddress && rva < section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData))
                {
                    return stream.Seek(section.PointerToRawData + (rva - section.VirtualAddress), SeekOrigin.Begin);
                }
            }

            throw new BadImageFormatException($"Address 0x{rva:X} is outside every section.");
        }

        string Text(int rva)
        {
            Seek(rva);
            var bytes = new List<byte>();
            for (var b = reader.ReadByte(); b != 0 && bytes.Count < 512; b = reader.ReadByte())
            {
                bytes.Add(b);
            }

            return Encoding.ASCII.GetString([.. bytes]);
        }

        var result = new List<(string, List<string>)>();
        var table = pe.ImportTableDirectory;
        for (var entry = table.RelativeVirtualAddress; table.Size > 0; entry += 20)
        {
            Seek(entry);
            var names = reader.ReadInt32();
            reader.ReadInt64();
            var library = reader.ReadInt32();
            var thunks = reader.ReadInt32();
            if (library == 0)
            {
                break;
            }

            var functions = new List<string>();
            var thunk = names != 0 ? names : thunks;
            for (var i = 0; i < 100_000; i++)
            {
                Seek(thunk + (i * (wide ? 8 : 4)));
                var value = wide ? reader.ReadInt64() : reader.ReadUInt32();
                if (value == 0)
                {
                    break;
                }

                var byOrdinal = wide ? value < 0 : (value & 0x8000_0000) != 0;
                if (!byOrdinal)
                {
                    functions.Add(Text((int)(value & 0x7FFF_FFFF) + 2));
                }
            }

            result.Add((Text(library), functions));
        }

        return result;
    }

    private static int? Read(IntResult? function) => function is not null && function(out var value) == 0 ? value : null;

    private static T? Function<T>(IntPtr library, string name)
        where T : Delegate =>
        library != IntPtr.Zero && NativeLibrary.TryGetExport(library, name, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    /// <summary>Where a loaded library came from, or null.</summary>
    private static string? PathOf(string library)
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>()
            .FirstOrDefault(m => string.Equals(m.ModuleName, library, StringComparison.OrdinalIgnoreCase))?.FileName;
    }

    // 12040 is CUDA 12.4.
    private static string CudaVersion(int version) =>
        string.Create(CultureInfo.InvariantCulture, $"{version / 1000}.{version % 1000 / 10}");

    // cuDNN 9 reports 90501 for 9.5.1.
    private static string CudnnVersion(nuint version) =>
        string.Create(CultureInfo.InvariantCulture, $"{version / 10000}.{version % 10000 / 100}.{version % 100}");
}
