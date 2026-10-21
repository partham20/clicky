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
internal sealed class VoiceStartup(
    VoiceInput voice,
    VoiceOutput speech,
    WhisperSpeechToText whisper,
    ModelDownloader downloader,
    ISettingsStore settings,
    IUserNotifier notifier,
    ILogger<VoiceStartup> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private int _warming;
    private WhisperModel? _downloading;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        whisper.DownloadProgressChanged += OnDownloadProgress;
        settings.Changed += OnSettingsChanged;
        WarmUp();

        // Alongside, not after: a first-run model download can take minutes.
        logger.LogInformation("Reply voice: {Engine}", settings.Current.Speech.Engine);
        _ = Task.Run(() => speech.WarmUpAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= OnSettingsChanged;
        whisper.DownloadProgressChanged -= OnDownloadProgress;
        await _stopping.CancelAsync();
    }

    public void Dispose() => _stopping.Dispose();

    private static bool AffectsEngine(VoiceSettings a, VoiceSettings b) =>
        a.Engine != b.Engine || a.LocalModel != b.LocalModel || a.CpuModel != b.CpuModel || a.Language != b.Language;

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (AffectsEngine(e.Previous.Voice, e.Current.Voice))
        {
            WarmUp();
        }

        var (before, after) = (e.Previous.Speech, e.Current.Speech);
        if (before.Engine != after.Engine || before.WindowsVoice != after.WindowsVoice)
        {
            _ = Task.Run(() => speech.WarmUpAsync(_stopping.Token), CancellationToken.None);
        }
    }

    private void WarmUp()
    {
        // One at a time; a change during a warm-up is picked up by the next Talk anyway.
        if (Interlocked.Exchange(ref _warming, 1) != 0)
        {
            return;
        }

        _ = Task.Run(
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
}
