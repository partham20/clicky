using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Clicky.Core.Voice;

/// <summary>
/// Text-to-speech with an open neural voice on this PC: Kokoro-82M by default, or any sherpa-onnx
/// Kokoro or Piper (VITS) model folder. CPU only, so it never competes with Whisper for the GPU.
/// Free and offline once the model (about 350 MB) is downloaded; the reply text never leaves the PC.
/// </summary>
/// <remarks>
/// <para>About 0.3 to 0.4 times real time on four threads of a 2.8 GHz Xeon, so the first sentence
/// of a reply takes 0.5 to 1 s and later ones are ready before the one ahead of them ends. The model
/// takes about 700 MB of memory while loaded.</para>
/// <para>The model downloads on first use, in the background; until it is ready a reply fails with
/// a message saying so, and shows as text. One synthesis at a time: a sentence that is cancelled
/// mid-way still finishes inside ONNX Runtime (it cannot be stopped), and the next waits for it.</para>
/// </remarks>
public sealed class LocalTextToSpeech(LocalVoiceDownloader downloader, ISettingsStore settings, ILogger<LocalTextToSpeech> logger) : ITextToSpeech, IDisposable
{
    /// <summary>The most threads picked automatically: beyond four Kokoro barely gets faster, and the PC stays responsive.</summary>
    public const int MaxAutoThreads = 4;

    private static readonly FieldInfo? HandleField = typeof(OfflineTts).GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _downloadLock = new();
    private OfflineTts? _tts;
    private LoadedModel? _loaded;
    private Task? _download;
    private LocalVoiceDownloadProgress? _progress;
    private string? _failure;
    private bool _disposed;

    /// <summary>Raised during a model download, then once with null when it ends (done or failed).</summary>
    public event EventHandler<LocalVoiceDownloadProgress?>? DownloadProgressChanged;

    /// <summary>The download in progress, if any.</summary>
    public LocalVoiceDownloadProgress? Download => _progress;

    public string Description
    {
        get
        {
            var speech = settings.Current.Speech;
            var voice = string.IsNullOrEmpty(speech.LocalVoice) ? LocalVoiceModels.DefaultVoice : speech.LocalVoice;
            return string.IsNullOrEmpty(speech.LocalModelPath)
                ? $"Local voice {LocalVoiceModels.Find(speech.LocalModel)?.Title ?? speech.LocalModel} ({voice})"
                : $"Local voice {Path.GetFileName(speech.LocalModelPath.TrimEnd('/', '\\'))} ({voice})";
        }
    }

    /// <summary>Threads for <paramref name="setting"/>: as set, or half the logical cores (1 to <see cref="MaxAutoThreads"/>) for 0.</summary>
    public static int ThreadsFor(int setting, int processors) =>
        setting > 0 ? setting : Math.Clamp(processors / 2, 1, MaxAutoThreads);

    /// <summary>
    /// What kind of sherpa-onnx model is in <paramref name="folder"/>, from its files; null if it is
    /// not one Clicky can load. Checked before loading, because sherpa-onnx only logs a bad config.
    /// </summary>
    public static LocalModelKind? DetectKind(string folder)
    {
        if (!Directory.Exists(folder) || !File.Exists(Path.Combine(folder, "tokens.txt")) || !Directory.Exists(Path.Combine(folder, "espeak-ng-data")))
        {
            return null;
        }

        if (File.Exists(Path.Combine(folder, "model.onnx")) && File.Exists(Path.Combine(folder, "voices.bin")))
        {
            return LocalModelKind.Kokoro;
        }

        // Piper voices: en_US-lessac-medium.onnx next to en_US-lessac-medium.onnx.json.
        return Directory.EnumerateFiles(folder, "*.onnx").Any(f => File.Exists(f + ".json")) ? LocalModelKind.Vits : null;
    }

    /// <summary>The sherpa-onnx configuration for a model folder and voice.</summary>
    internal static OfflineTtsConfig ConfigFor(string folder, LocalModelKind kind, string voice, int threads)
    {
        var config = new OfflineTtsConfig();
        config.Model.NumThreads = threads;
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;

        // Clicky hands over one sentence at a time already.
        config.MaxNumSentences = 1;
        if (kind == LocalModelKind.Kokoro)
        {
            config.Model.Kokoro.Model = Path.Combine(folder, "model.onnx");
            config.Model.Kokoro.Voices = Path.Combine(folder, "voices.bin");
            config.Model.Kokoro.Tokens = Path.Combine(folder, "tokens.txt");
            config.Model.Kokoro.DataDir = Path.Combine(folder, "espeak-ng-data");

            // Kokoro v1.0 reads English best from its lexicons (espeak-ng covers the words they lack);
            // the older English-only export has none and needs no language.
            var (language, lexicon) = LocalVoiceModels.LanguageOf(voice);
            if (File.Exists(Path.Combine(folder, "lexicon-us-en.txt")))
            {
                config.Model.Kokoro.Lang = language;
                if (lexicon is not null && File.Exists(Path.Combine(folder, lexicon)))
                {
                    config.Model.Kokoro.Lexicon = Path.Combine(folder, lexicon);
                }

                if (Directory.Exists(Path.Combine(folder, "dict")))
                {
                    config.Model.Kokoro.DictDir = Path.Combine(folder, "dict");
                }
            }
        }
        else
        {
            config.Model.Vits.Model = Directory.EnumerateFiles(folder, "*.onnx").Where(f => File.Exists(f + ".json")).Order(StringComparer.Ordinal).First();
            config.Model.Vits.Tokens = Path.Combine(folder, "tokens.txt");
            config.Model.Vits.DataDir = Path.Combine(folder, "espeak-ng-data");
        }

        return config;
    }

    public async Task<SpeechClip> SynthesizeAsync(string text, SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var speech = settings.Current.Speech;
        var folder = await ResolveFolderAsync(speech, wait: false, cancellationToken).ConfigureAwait(false);
        var voice = string.IsNullOrWhiteSpace(request.Voice) ? speech.LocalVoice : request.Voice;
        var threads = ThreadsFor(speech.LocalThreads, Environment.ProcessorCount);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<SpeechClip> work;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            work = Task.Run(() => Synthesize(folder, voice, threads, text, request.Rate), CancellationToken.None);
        }
        catch
        {
            _gate.Release();
            throw;
        }

        // Native synthesis cannot be interrupted: keep the model locked until it really ends.
        _ = work.ContinueWith(_ => _gate.Release(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            await ResolveFolderAsync(settings.Current.Speech, wait: true, cancellationToken).ConfigureAwait(false);
            var clip = await SynthesizeAsync("Ready.", new SpeechRequest(string.Empty, 1.0), cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "{Voice} ready in {Ms:0} ms ({Rate} Hz, {Threads} threads, \"Ready.\" took {SynthMs:0} ms)",
                Description,
                stopwatch.Elapsed.TotalMilliseconds,
                clip.SampleRate,
                ThreadsFor(settings.Current.Speech.LocalThreads, Environment.ProcessorCount),
                clip.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TextToSpeechException ex)
        {
            logger.LogWarning(ex, "Local voice warm-up failed");
        }
    }

    public void Dispose()
    {
        // Let a synthesis in flight finish before its model is freed under it.
        var locked = _gate.Wait(TimeSpan.FromSeconds(5));
        _disposed = true;
        if (locked)
        {
            _tts?.Dispose();
            _tts = null;
            _gate.Release();
        }
    }

    /// <summary>
    /// The model folder to use: the configured path, or the downloaded model. Starts the download if
    /// it is missing; <paramref name="wait"/> waits for it, otherwise this throws saying it is on its way.
    /// </summary>
    private async Task<string> ResolveFolderAsync(SpeechSettings speech, bool wait, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(speech.LocalModelPath))
        {
            return speech.LocalModelPath;
        }

        var model = LocalVoiceModels.Find(speech.LocalModel) ?? throw new TextToSpeechException(
            $"Unknown local voice model \"{speech.LocalModel}\". Known models: {string.Join(", ", LocalVoiceModels.All.Select(m => m.Name))}.");
        if (downloader.IsAvailable(model))
        {
            return downloader.PathOf(model);
        }

        if (!wait && Interlocked.Exchange(ref _failure, null) is { } failure)
        {
            // Said once; the next reply starts the download again.
            throw new TextToSpeechException($"The natural voice could not be downloaded: {failure} Clicky tries again on the next reply.");
        }

        var download = StartDownload(model);
        if (!wait)
        {
            var percent = _progress is { } p ? $" ({(p.Unpacking ? "unpacking" : $"{p.Fraction:P0}")})" : string.Empty;
            throw new TextToSpeechException($"The natural voice is still downloading{percent}. Replies show as text until it's ready.");
        }

        await download.WaitAsync(cancellationToken).ConfigureAwait(false);
        return downloader.IsAvailable(model)
            ? downloader.PathOf(model)
            : throw new TextToSpeechException($"The natural voice could not be downloaded: {Interlocked.Exchange(ref _failure, null) ?? "no details."}");
    }

    private Task StartDownload(LocalVoiceModel model)
    {
        lock (_downloadLock)
        {
            if (_download is { IsCompleted: false } running)
            {
                return running;
            }

            var progress = new Progress(this);
            _download = Task.Run(async () =>
            {
                try
                {
                    await downloader.EnsureAsync(model, progress).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TextToSpeechException or IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Voice model {Model} download failed", model.Name);
                    Volatile.Write(ref _failure, ex.Message);
                }
                finally
                {
                    _progress = null;
                    DownloadProgressChanged?.Invoke(this, null);
                }
            });
            return _download;
        }
    }

    // On the thread pool, under _gate.
    private SpeechClip Synthesize(string folder, string voice, int threads, string text, double rate)
    {
        var stopwatch = Stopwatch.StartNew();
        var tts = Load(folder, voice, threads);
        var speaker = SpeakerFor(voice, tts.NumSpeakers);
        var audio = tts.Generate(text, (float)Math.Clamp(rate, SpeechSettings.MinRate, SpeechSettings.MaxRate), speaker);
        try
        {
            if (audio.Handle == IntPtr.Zero)
            {
                // Nothing it can pronounce (a lone emoji, a script the voice has no rules for): skip the sentence.
                logger.LogWarning("Local voice could not read a {Length}-character sentence; skipped", text.Length);
                return new SpeechClip([], tts.SampleRate, stopwatch.Elapsed);
            }

            return new SpeechClip(audio.Samples, audio.SampleRate, stopwatch.Elapsed);
        }
        finally
        {
            // Not IDisposable, but holds the native buffer.
            audio.Dispose();
        }
    }

    private OfflineTts Load(string folder, string voice, int threads)
    {
        var kind = DetectKind(folder) ?? throw new TextToSpeechException(
            $"{folder} is not a sherpa-onnx voice model (it needs tokens.txt, espeak-ng-data and a Kokoro or Piper .onnx file).");

        // A voice in another language reads with other rules, so it needs its own instance.
        var wanted = new LoadedModel(folder, kind, kind == LocalModelKind.Kokoro ? LocalVoiceModels.LanguageOf(voice).Language : string.Empty, threads);
        if (_tts is not null && _loaded == wanted)
        {
            return _tts;
        }

        _tts?.Dispose();
        _tts = null;
        _loaded = null;
        var stopwatch = Stopwatch.StartNew();
        var tts = new OfflineTts(ConfigFor(folder, kind, voice, threads));
        if (HandleField?.GetValue(tts) is HandleRef { Handle: 0 })
        {
            throw new TextToSpeechException($"The local voice model in {folder} could not be loaded; the log says why.");
        }

        _tts = tts;
        _loaded = wanted;
        logger.LogInformation(
            "Local voice loaded from {Folder} ({Kind}, {Language}, {Speakers} speakers, {Threads} threads) in {Ms:0} ms",
            folder,
            kind,
            wanted.Language,
            tts.NumSpeakers,
            threads,
            stopwatch.Elapsed.TotalMilliseconds);
        return tts;
    }

    private int SpeakerFor(string voice, int speakers)
    {
        var speaker = LocalVoiceModels.SpeakerOf(voice);
        if (speaker is null || speaker >= speakers)
        {
            logger.LogWarning("Local voice {Voice} is not in this model ({Speakers} speakers); using the first", voice, speakers);
            return 0;
        }

        return speaker.Value;
    }

    private sealed record LoadedModel(string Folder, LocalModelKind Kind, string Language, int Threads);

    private sealed class Progress(LocalTextToSpeech owner) : IProgress<LocalVoiceDownloadProgress>
    {
        public void Report(LocalVoiceDownloadProgress value)
        {
            owner._progress = value;
            owner.DownloadProgressChanged?.Invoke(owner, value);
        }
    }
}

/// <summary>The sherpa-onnx model families <see cref="LocalTextToSpeech"/> loads.</summary>
public enum LocalModelKind
{
    /// <summary>Kokoro-82M: model.onnx, voices.bin, tokens.txt, espeak-ng-data, lexicons.</summary>
    Kokoro,

    /// <summary>Piper and other VITS voices: name.onnx with name.onnx.json, tokens.txt, espeak-ng-data.</summary>
    Vits,
}
