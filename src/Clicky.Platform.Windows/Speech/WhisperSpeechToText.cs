using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace Clicky.Platform.Windows.Speech;

/// <summary>
/// Local speech-to-text with whisper.cpp (Whisper.net): on the GPU through Vulkan when it loads,
/// otherwise on the CPU with a smaller English model. The model is downloaded on first use and
/// loaded once; requests are serialized because a Whisper context is single-threaded.
/// </summary>
/// <remarks>
/// <para>Vulkan rather than CUDA: it runs on NVIDIA, AMD and Intel GPUs with only the graphics
/// driver, while the CUDA runtime adds hundreds of MB and needs a matching toolkit. On a laptop
/// with both, Vulkan lists the integrated GPU first; the first discrete one is used instead.</para>
/// <para>Flash attention is on for the GPU: on an RTX A1000 it takes a 6 s question from about
/// 1.0 s to 0.6 s with the same text. Shrinking the audio context to the clip length would be
/// faster still, but makes large-v3-turbo repeat itself, and every new size compiles new GPU
/// pipelines (seconds each), so the full 30 s window is kept.</para>
/// <para>On the CPU the audio context is cut to the clip instead (see <see cref="ProcessorFor"/>).</para>
/// <para>The native runtime is chosen once per process, so voice.useGpu takes effect on restart. A
/// model change applies to the next request.</para>
/// </remarks>
public sealed partial class WhisperSpeechToText : ISpeechToText, IDisposable
{
    // whisper.cpp ignores input under 1 s ("input is too short"); pad short utterances with silence.
    private static readonly int MinimumSamples = SpeechAudio.SamplesIn(TimeSpan.FromSeconds(1.1));

    private const int FullAudioContext = 1500;
    private const int MinAudioContext = 256;

    private static readonly Lock RuntimeLock = new();
    private static readonly List<VulkanDevice> s_vulkanDevices = [];
    private static bool s_runtimeChosen;
    private static int s_gpuDevice;

    private readonly ModelDownloader _downloader;
    private readonly ISettingsStore _settings;
    private readonly ILogger<WhisperSpeechToText> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Dictionary<string, WhisperProcessor> _processors = new(StringComparer.Ordinal);

    private WhisperFactory? _factory;
    private WhisperModel? _model;
    private bool _onGpu;
    private ModelDownloadProgress? _download;
    private bool _disposed;

    public WhisperSpeechToText(ModelDownloader downloader, ISettingsStore settings, ILogger<WhisperSpeechToText> logger)
    {
        _downloader = downloader;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Raised (on a worker thread) while a model downloads, and with null when it is done.</summary>
    public event EventHandler<ModelDownloadProgress?>? DownloadProgressChanged;

    public string Description => _model is { } model
        ? $"Whisper {model.Name} ({(_onGpu ? "GPU" : "CPU")})"
        : $"Whisper {_settings.Current.Voice.LocalModel}";

    /// <summary>True when the native runtime that loaded can use the GPU.</summary>
    public static bool GpuRuntimeLoaded => RuntimeOptions.LoadedLibrary == RuntimeLibrary.Vulkan;

    public async Task<Transcript> TranscribeAsync(ReadOnlyMemory<float> samples, string language, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_download is { } download)
        {
            throw new SpeechToTextException(
                $"The speech model is still downloading ({download.Fraction:0%} of {download.Model.SizeText}). Talk will work as soon as it finishes.");
        }

        await LoadAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var audio = samples.Length >= MinimumSamples ? samples.ToArray() : Pad(samples.Span);
            var stopwatch = Stopwatch.StartNew();
            var processor = ProcessorFor(language, audio.Length, out var effectiveLanguage, out var reusable);
            try
            {
                var parts = new List<string>();
                string? detected = null;
                await foreach (var segment in processor.ProcessAsync(audio, cancellationToken).ConfigureAwait(false))
                {
                    parts.Add(segment.Text.Trim());
                    detected ??= segment.Language;
                }

                return new Transcript(string.Join(' ', parts.Where(p => p.Length > 0)), detected ?? effectiveLanguage, Description, stopwatch.Elapsed);
            }
            finally
            {
                if (!reusable)
                {
                    await processor.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or SpeechToTextException or ObjectDisposedException))
        {
            throw new SpeechToTextException($"Whisper failed: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);

            // The first run compiles GPU pipelines and allocates buffers: about a second we would
            // otherwise add to the first Talk.
            var stopwatch = Stopwatch.StartNew();
            await TranscribeAsync(new float[MinimumSamples], _settings.Current.Voice.Language, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Engine} warmed up in {Ms:0} ms", Description, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is SpeechToTextException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Local speech-to-text is not ready");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Wait();
        try
        {
            Unload();
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
        _loadGate.Dispose();
    }

    private static float[] Pad(ReadOnlySpan<float> samples)
    {
        var padded = new float[MinimumSamples];
        samples.CopyTo(padded);
        return padded;
    }

    /// <summary>Picks the native runtime once per process: Vulkan then CPU, or CPU only.</summary>
    private bool ChooseRuntime(bool useGpu)
    {
        lock (RuntimeLock)
        {
            if (!s_runtimeChosen)
            {
                s_runtimeChosen = true;
                RuntimeOptions.RuntimeLibraryOrder = useGpu ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx] : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
                LogProvider.AddLogger(OnWhisperLog);

                // Loads the native library, which logs the Vulkan devices it finds (see OnWhisperLog).
                var info = WhisperFactory.GetRuntimeInfo();
                _logger.LogInformation("Whisper runtime {Library}: {Info}", RuntimeOptions.LoadedLibrary, info?.Trim());

                var device = s_vulkanDevices.Find(d => d.Discrete) ?? s_vulkanDevices.FirstOrDefault();
                if (device is not null)
                {
                    s_gpuDevice = device.Index;
                    _logger.LogInformation(
                        "Whisper GPU: {Name} (Vulkan device {Index} of {Count}{Kind})",
                        device.Name,
                        device.Index,
                        s_vulkanDevices.Count,
                        device.Discrete ? ", discrete" : ", integrated");
                }
            }

            return GpuRuntimeLoaded;
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var voice = _settings.Current.Voice;
        if (_factory is not null && _model is not null && WantedModel(voice, _onGpu) == _model)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var onGpu = ChooseRuntime(voice.UseGpu) && voice.UseGpu;
            var model = WantedModel(voice, onGpu)
                ?? throw new SpeechToTextException(
                    $"Unknown Whisper model \"{(onGpu ? voice.LocalModel : voice.CpuModel)}\". Known models: {string.Join(", ", WhisperModels.All.Select(m => m.Name))}.");
            if (_factory is not null && _model == model)
            {
                return;
            }

            var path = await DownloadAsync(model, cancellationToken).ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            var factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = onGpu, GpuDevice = s_gpuDevice, UseFlashAttention = onGpu });

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Unload();
                _factory = factory;
                _model = model;
                _onGpu = onGpu;
            }
            finally
            {
                _gate.Release();
            }

            _logger.LogInformation("Loaded {Engine} in {Ms:0} ms", Description, stopwatch.Elapsed.TotalMilliseconds);
            if (voice.UseGpu && !onGpu)
            {
                _logger.LogWarning("The GPU (Vulkan) runtime did not load, so Whisper runs on the CPU with {Model}", model.Name);
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or SpeechToTextException))
        {
            throw new SpeechToTextException($"Could not load the Whisper model: {ex.Message}", ex);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private static WhisperModel? WantedModel(VoiceSettings voice, bool onGpu) =>
        WhisperModels.Find(onGpu ? voice.LocalModel : voice.CpuModel);

    private async Task<string> DownloadAsync(WhisperModel model, CancellationToken cancellationToken)
    {
        if (_downloader.IsAvailable(model))
        {
            return _downloader.PathOf(model);
        }

        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            _download = p;
            DownloadProgressChanged?.Invoke(this, p);
        });

        _download = new ModelDownloadProgress(model, 0);
        DownloadProgressChanged?.Invoke(this, _download);
        try
        {
            return await _downloader.EnsureAsync(model, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _download = null;
            DownloadProgressChanged?.Invoke(this, null);
        }
    }

    /// <summary>
    /// On the GPU, one processor per language, reused, over the full 30 s window. On the CPU, a
    /// fresh one per request with the audio context cut to the clip's length: the encoder's cost
    /// scales with it, which takes small.en from about 2.5 s to 0.5 s for a short question. (On
    /// the CPU nothing has to be compiled per size, and the .en models do not repeat themselves
    /// the way large-v3-turbo does.)
    /// </summary>
    private WhisperProcessor ProcessorFor(string language, int samples, out string effective, out bool reusable)
    {
        var model = _model!;
        effective = string.IsNullOrWhiteSpace(language) ? VoiceSettings.DefaultLanguage : language;
        if (model.EnglishOnly && effective != "en")
        {
            _logger.LogDebug("{Model} only knows English; ignoring language {Language}", model.Name, effective);
            effective = "en";
        }

        reusable = _onGpu;
        if (reusable && _processors.TryGetValue(effective, out var cached))
        {
            return cached;
        }

        var builder = _factory!.CreateBuilder()
            .WithThreads(Math.Clamp(Environment.ProcessorCount - 4, 1, 12))
            .WithNoContext();
        builder = effective == VoiceSettings.AutoLanguage ? builder.WithLanguageDetection() : builder.WithLanguage(effective);
        if (!reusable)
        {
            // One unit is 20 ms of audio; 1500 is the full window. With less than ~5 s of context the
            // decoder sometimes falls back to sampling at higher temperatures, which costs seconds.
            builder = builder.WithAudioContextSize(Math.Clamp((samples / 320) + 64, MinAudioContext, FullAudioContext));
        }

        var processor = builder.Build();
        if (reusable)
        {
            _processors[effective] = processor;
        }

        return processor;
    }

    private void Unload()
    {
        foreach (var processor in _processors.Values)
        {
            processor.Dispose();
        }

        _processors.Clear();
        _factory?.Dispose();
        _factory = null;
        _model = null;
    }

    private void OnWhisperLog(WhisperLogLevel level, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var text = message.TrimEnd();
        if (VulkanDeviceLine().Match(text) is { Success: true } match)
        {
            lock (s_vulkanDevices)
            {
                s_vulkanDevices.Add(new VulkanDevice(
                    int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture),
                    match.Groups["name"].Value.Trim(),
                    match.Groups["uma"].Value == "0"));
            }
        }

        if (level == WhisperLogLevel.Error)
        {
            _logger.LogWarning("whisper.cpp: {Message}", text);
        }
        else if (text.Contains("vulkan", StringComparison.OrdinalIgnoreCase) || text.Contains("ggml_", StringComparison.Ordinal))
        {
            _logger.LogDebug("whisper.cpp: {Message}", text);
        }
    }

    // "ggml_vulkan: 1 = NVIDIA RTX A1000 6GB Laptop GPU (NVIDIA) | uma: 0 | fp16: 1 | ..."; uma 1 means
    // the GPU shares system memory (integrated).
    [GeneratedRegex(@"ggml_vulkan: (?<index>\d+) = (?<name>[^|]+?) \|\s*uma: (?<uma>\d)", RegexOptions.CultureInvariant)]
    private static partial Regex VulkanDeviceLine();

    private sealed record VulkanDevice(int Index, string Name, bool Discrete);
}
