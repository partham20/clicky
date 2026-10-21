using System.Diagnostics;
using Clicky.Core.Voice;
using Microsoft.Extensions.Logging;
using Windows.Media.SpeechSynthesis;

namespace Clicky.Platform.Windows.Speech;

/// <summary>
/// Text-to-speech with the voices installed in Windows (WinRT SpeechSynthesizer, the OneCore
/// voices under Settings › Time &amp; language › Speech). Free, offline, and quick: about 100 ms
/// for a sentence once the voice is loaded.
/// </summary>
/// <remarks>
/// <para>The newer "natural" Narrator voices are not offered to apps through this API, so on most
/// PCs the choice is the classic set (David, Zira, Mark, plus whatever language packs added). The
/// cloud engine is the option for a natural voice.</para>
/// <para>One synthesizer, one request at a time; the speech pipeline only asks for one sentence
/// ahead anyway. Each sentence comes back as a WAV in memory, never on disk.</para>
/// </remarks>
public sealed class WindowsTextToSpeech(ILogger<WindowsTextToSpeech> logger) : ITextToSpeech, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SpeechSynthesizer? _synthesizer;
    private string _voiceId = string.Empty;

    public string Description
    {
        get
        {
            try
            {
                return $"Windows voice {(Volatile.Read(ref _synthesizer)?.Voice ?? SpeechSynthesizer.DefaultVoice).DisplayName}";
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                return "Windows voice";
            }
        }
    }

    /// <summary>Installed voices, default first, then by language and name.</summary>
    public static IReadOnlyList<VoiceInfo> ListVoices()
    {
        var defaultId = SpeechSynthesizer.DefaultVoice?.Id;
        return
        [
            .. SpeechSynthesizer.AllVoices
                .Select(v => new VoiceInfo(v.Id, v.DisplayName, v.Language, v.Id == defaultId))
                .OrderByDescending(v => v.IsDefault)
                .ThenBy(v => v.Language, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    public async Task<SpeechClip> SynthesizeAsync(string text, SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var synthesizer = Prepare(request);
            byte[] wav;
            using (var stream = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(cancellationToken).ConfigureAwait(false))
            {
                using var reader = stream.AsStreamForRead();
                using var memory = new MemoryStream(checked((int)stream.Size));
                await reader.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                wav = memory.ToArray();
            }

            var (samples, rate) = WavDecoder.Decode(wav);
            return new SpeechClip(samples, rate, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new TextToSpeechException($"The Windows voice failed: {ex.Message}", ex);
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
            var stopwatch = Stopwatch.StartNew();
            var clip = await SynthesizeAsync("Ready.", new SpeechRequest(_voiceId, 1.0), cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "{Voice} ready in {Ms:0} ms ({Rate} Hz; {Count} voices installed)",
                Description,
                stopwatch.Elapsed.TotalMilliseconds,
                clip.SampleRate,
                SpeechSynthesizer.AllVoices.Count);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TextToSpeechException ex)
        {
            logger.LogWarning(ex, "Windows voice warm-up failed");
        }
    }

    public void Dispose()
    {
        _synthesizer?.Dispose();
        _gate.Dispose();
    }

    private SpeechSynthesizer Prepare(SpeechRequest request)
    {
        if (_synthesizer is null)
        {
            var created = new SpeechSynthesizer();

            // Pauses between sentences are the pipeline's; trailing silence would only add to them.
            created.Options.AppendedSilence = SpeechAppendedSilence.Min;
            Volatile.Write(ref _synthesizer, created);
        }

        var synthesizer = _synthesizer;
        if (!string.Equals(_voiceId, request.Voice, StringComparison.Ordinal))
        {
            _voiceId = request.Voice;
            var voice = string.IsNullOrEmpty(request.Voice) ? null : SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == request.Voice);
            if (voice is null && !string.IsNullOrEmpty(request.Voice))
            {
                logger.LogWarning("Windows voice {Voice} is not installed; using the default", request.Voice);
            }

            synthesizer.Voice = voice ?? SpeechSynthesizer.DefaultVoice;
            logger.LogDebug("Windows voice set to {Voice}", synthesizer.Voice.DisplayName);
        }

        // SpeakingRate's range is 0.5 to 6.0.
        synthesizer.Options.SpeakingRate = Math.Clamp(request.Rate, 0.5, 6.0);
        return synthesizer;
    }
}
