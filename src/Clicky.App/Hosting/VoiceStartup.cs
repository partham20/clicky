using System.Windows.Threading;
using Clicky.Core.Capture;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Clicky.Platform.Windows.Speech;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// Gets speech-to-text ready in the background at startup (model download, load, GPU warm-up) and
/// again when settings.voice changes engine or model, and tells the user about the one-time model
/// download so a first Talk that says "still downloading" is no surprise. Loads the reply voice
/// too, at startup and when settings.speech changes engine or voice.
/// </summary>
/// <remarks>
/// The screen-capture path (a D3D11 device) warms up here too, first and on its own: on Optimus
/// laptops creating it while Whisper brought up Vulkan crashed about one start in six. So startup
/// waits for the UI to settle, warms capture, and only then speech-to-text (see GpuInitGate).
/// </remarks>
internal sealed class VoiceStartup(
    ICaptureService capture,
    Dispatcher dispatcher,
    VoiceInput voice,
    VoiceOutput speech,
    WhisperSpeechToText whisper,
    ModelDownloader downloader,
    LocalTextToSpeech localVoice,
    LocalVoiceDownloader voiceDownloader,
    ISettingsStore settings,
    IUserNotifier notifier,
    ILogger<VoiceStartup> logger) : IHostedService, IDisposable
{
    /// <summary>After the UI goes idle, room for WPF's render thread to create its own device.</summary>
    private static readonly TimeSpan UiSettle = TimeSpan.FromMilliseconds(500);

    private readonly CancellationTokenSource _stopping = new();
    private int _warming;
    private WhisperModel? _downloading;
    private LocalVoiceModel? _downloadingVoice;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        whisper.DownloadProgressChanged += OnDownloadProgress;
        localVoice.DownloadProgressChanged += OnVoiceDownloadProgress;
        settings.Changed += OnSettingsChanged;
        _ = Task.Run(WarmUpGpuUsersAsync, CancellationToken.None);

        // Alongside, not after: a first-run model download can take minutes. No GPU involved.
        logger.LogInformation("Reply voice: {Engine}", settings.Current.Speech.Engine);
        _ = Task.Run(() => speech.WarmUpAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= OnSettingsChanged;
        whisper.DownloadProgressChanged -= OnDownloadProgress;
        localVoice.DownloadProgressChanged -= OnVoiceDownloadProgress;
        await _stopping.CancelAsync();
    }

    public void Dispose() => _stopping.Dispose();

    private static bool AffectsEngine(VoiceSettings a, VoiceSettings b) =>
        a.Engine != b.Engine || a.LocalModel != b.LocalModel || a.CpuModel != b.CpuModel || a.Language != b.Language;

    private static bool AffectsLocalVoice(SpeechSettings a, SpeechSettings b) =>
        a.LocalModel != b.LocalModel || a.LocalModelPath != b.LocalModelPath || a.LocalThreads != b.LocalThreads
        || LocalVoiceModels.LanguageOf(a.LocalVoice) != LocalVoiceModels.LanguageOf(b.LocalVoice);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (AffectsEngine(e.Previous.Voice, e.Current.Voice))
        {
            _ = WarmUp();
        }

        var (before, after) = (e.Previous.Speech, e.Current.Speech);
        if (before.Engine != after.Engine
            || before.WindowsVoice != after.WindowsVoice
            || (after.Engine == VoiceOutputEngine.Local && AffectsLocalVoice(before, after)))
        {
            _ = Task.Run(() => speech.WarmUpAsync(_stopping.Token), CancellationToken.None);
        }
    }

    private async Task WarmUpGpuUsersAsync()
    {
        try
        {
            // Not from StartAsync itself: the UI thread is blocked until the host has started.
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task.WaitAsync(_stopping.Token).ConfigureAwait(false);
            await Task.Delay(UiSettle, _stopping.Token).ConfigureAwait(false);

            // Device creation and codec loading cost ~1 s once; pay it now rather than on the first Talk.
            await capture.WarmUpAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Capture warm-up failed; the first capture will be slower");
        }

        await WarmUp().ConfigureAwait(false);
    }

    private Task WarmUp()
    {
        // One at a time; a change during a warm-up is picked up by the next Talk anyway.
        if (Interlocked.Exchange(ref _warming, 1) != 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(
            async () =>
            {
                try
                {
                    logger.LogInformation("Speech-to-text: {Engine}, language {Language}", settings.Current.Voice.Engine, settings.Current.Voice.Language);
                    await voice.WarmUpAsync(_stopping.Token).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref _warming, 0);
                }
            },
            CancellationToken.None);
    }

    private void OnDownloadProgress(object? sender, ModelDownloadProgress? progress)
    {
        if (progress is { Received: 0 } started)
        {
            _downloading = started.Model;
            notifier.Notify(
                "Downloading the speech model",
                $"Clicky is downloading {started.Model.Name} ({started.Model.SizeText}) once, so speech-to-text can run on this PC. Talk works as soon as it finishes.");
        }
        else if (progress is null && _downloading is { } model)
        {
            // A failed download is logged, and explained on the next Talk.
            _downloading = null;
            if (downloader.IsAvailable(model))
            {
                notifier.Notify("Speech model ready", "Talk is ready: hold Ctrl+Alt and speak.");
            }
        }
    }

    private void OnVoiceDownloadProgress(object? sender, LocalVoiceDownloadProgress? progress)
    {
        if (progress is { Received: 0, Unpacking: false } started)
        {
            _downloadingVoice = started.Model;
            notifier.Notify(
                "Downloading the natural voice",
                $"Clicky is downloading {started.Model.Title} ({started.Model.SizeText}) once, so replies can be spoken by a natural voice on this PC. Until then they show as text.");
        }
        else if (progress is null && _downloadingVoice is { } model)
        {
            // A failed download is logged, and explained on the next reply.
            _downloadingVoice = null;
            if (voiceDownloader.IsAvailable(model))
            {
                notifier.Notify("Natural voice ready", "Replies are spoken by the new voice from now on.");
            }
        }
    }
}
