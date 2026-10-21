namespace Clicky.Core.Voice;

/// <summary>Which engine speaks replies. Written to settings.json; do not rename.</summary>
public enum VoiceOutputEngine
{
    /// <summary>The voices installed in Windows (WinRT SpeechSynthesizer): free, offline, about 100 ms a sentence.</summary>
    Windows,

    /// <summary>A hosted speech API (OpenAI). Natural voices; needs an API key and sends the reply text out.</summary>
    Cloud,

    /// <summary>
    /// A neural voice running on this PC (Kokoro-82M through sherpa-onnx, CPU only): natural, offline
    /// and free, after a one-time model download. See <see cref="LocalTextToSpeech"/>.
    /// </summary>
    Local,
}

/// <param name="Id">Engine-specific id, for settings.speech.windowsVoice.</param>
/// <param name="Name">Display name, e.g. "Microsoft Heera".</param>
/// <param name="Language">BCP-47 tag, e.g. "en-IN".</param>
/// <param name="IsDefault">The voice Windows uses when none is chosen.</param>
public sealed record VoiceInfo(string Id, string Name, string Language, bool IsDefault);

/// <param name="Voice">Windows voice id, cloud voice name or local voice name; empty for the engine's default.</param>
/// <param name="Rate">1 is normal speed.</param>
public readonly record struct SpeechRequest(string Voice, double Rate);

/// <summary>Synthesized speech for one sentence.</summary>
/// <param name="Samples">Mono, -1..1.</param>
/// <param name="SampleRate">Samples per second of <paramref name="Samples"/>.</param>
/// <param name="Elapsed">How long synthesis took.</param>
public sealed record SpeechClip(float[] Samples, int SampleRate, TimeSpan Elapsed)
{
    // About -54 dBFS: below any voiced sound, above the dither some engines add to silence.
    private const float SilenceThreshold = 0.002f;
    private static readonly TimeSpan SilenceMargin = TimeSpan.FromMilliseconds(15);

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);

    /// <summary>
    /// Cuts the leading and trailing silence engines add around each sentence (Windows voices start
    /// with ~100 ms of it), keeping a few ms so consonants are not clipped. Pauses between sentences
    /// are then the pipeline's to choose, and the first sound comes sooner.
    /// </summary>
    public SpeechClip TrimSilence()
    {
        var first = Array.FindIndex(Samples, s => Math.Abs(s) > SilenceThreshold);
        if (first < 0)
        {
            return this with { Samples = [] };
        }

        var last = Array.FindLastIndex(Samples, s => Math.Abs(s) > SilenceThreshold);
        var margin = (int)(SampleRate * SilenceMargin.TotalSeconds);
        var start = Math.Max(0, first - margin);
        var end = Math.Min(Samples.Length, last + 1 + margin);
        return start == 0 && end == Samples.Length ? this : this with { Samples = Samples[start..end] };
    }
}

/// <summary>Turns one sentence of plain text into audio.</summary>
public interface ITextToSpeech
{
    /// <summary>Short description of the engine and voice, for logs and Settings.</summary>
    string Description { get; }

    /// <summary>Synthesizes <paramref name="text"/>. Calls may be serialized.</summary>
    /// <exception cref="TextToSpeechException">The engine is not available or failed.</exception>
    Task<SpeechClip> SynthesizeAsync(string text, SpeechRequest request, CancellationToken cancellationToken = default);

    /// <summary>Loads the voice so the first reply is no slower than the rest. Safe to call more than once; never throws.</summary>
    Task WarmUpAsync(CancellationToken cancellationToken = default);
}

public sealed class TextToSpeechException : Exception
{
    public TextToSpeechException()
    {
    }

    public TextToSpeechException(string message)
        : base(message)
    {
    }

    public TextToSpeechException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
