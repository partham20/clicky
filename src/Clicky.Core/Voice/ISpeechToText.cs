namespace Clicky.Core.Voice;

/// <summary>Which speech-to-text engine turns Talk audio into text. Written to settings.json; do not rename.</summary>
public enum SpeechEngine
{
    /// <summary>Whisper on this PC: free, offline, audio never leaves the machine.</summary>
    Local,

    /// <summary>A hosted transcription API (OpenAI). Needs an API key and sends the audio out.</summary>
    Cloud,
}

/// <param name="Text">Raw engine text, segments joined with spaces. Clean it with <see cref="TranscriptCleaner"/>.</param>
/// <param name="Language">Language the engine used or detected (ISO 639-1), when it says.</param>
/// <param name="Engine">Human-readable engine and model, for logs, e.g. "Whisper large-v3-turbo-q5_0 (Vulkan)".</param>
/// <param name="Elapsed">Wall time for the request, excluding queueing behind another one.</param>
public sealed record Transcript(string Text, string? Language, string Engine, TimeSpan Elapsed);

/// <summary>Turns 16 kHz mono speech into text.</summary>
public interface ISpeechToText
{
    /// <summary>Short description of the engine and model, for logs and Settings.</summary>
    string Description { get; }

    /// <summary>
    /// Transcribes <paramref name="samples"/> (16 kHz mono, -1..1). Thread-safe; concurrent calls
    /// may be serialized.
    /// </summary>
    /// <param name="language">ISO 639-1 code, or "auto" to let the engine detect it.</param>
    /// <exception cref="SpeechToTextException">The engine is not ready or failed.</exception>
    Task<Transcript> TranscribeAsync(ReadOnlyMemory<float> samples, string language, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets ready for the first request (download and load a model, open connections) so that one
    /// is no slower than the rest. Safe to call more than once; never throws.
    /// </summary>
    Task WarmUpAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// A request is coming soon (the user just started talking): wake anything that is slow to
    /// wake, such as a GPU that has clocked down. Returns at once; never throws.
    /// </summary>
    void Prepare();
}

public sealed class SpeechToTextException : Exception
{
    public SpeechToTextException()
    {
    }

    public SpeechToTextException(string message)
        : base(message)
    {
    }

    public SpeechToTextException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
