namespace Clicky.Core.Voice;

/// <summary>The system microphone. Each recording opens the device fresh, so it follows default-device changes.</summary>
public interface IMicrophone
{
    /// <summary>
    /// Opens the input device and starts recording. The device keeps its own format (no exclusive
    /// mode, no format negotiation): <paramref name="createSink"/> is called once with it, before the
    /// first buffer, and gets every buffer after that. Returns once the device has started.
    /// </summary>
    /// <param name="deviceId">A device id, or empty for the Windows default input.</param>
    /// <exception cref="MicrophoneException">No usable device, access denied, or the device failed to start.</exception>
    Task<IMicrophoneRecording> StartAsync(string deviceId, Func<PcmFormat, SpeechAudioSink> createSink, CancellationToken cancellationToken = default);
}

/// <summary>A running recording. Dispose stops it and releases the device without keeping anything.</summary>
public interface IMicrophoneRecording : IAsyncDisposable
{
    /// <summary>Friendly device name, for logs and messages.</summary>
    string DeviceName { get; }

    PcmFormat Format { get; }

    /// <summary>Stops the device and waits for the last buffer. Returns null if it stopped cleanly, else why it failed.</summary>
    Task<MicrophoneException?> StopAsync();
}

public enum MicrophoneProblem
{
    Failed,

    /// <summary>No input device is plugged in or enabled.</summary>
    NoDevice,

    /// <summary>Windows privacy settings block desktop apps from the microphone.</summary>
    AccessDenied,

    /// <summary>Another app has the device in exclusive mode.</summary>
    InUse,

    /// <summary>The device was unplugged or disabled while recording.</summary>
    Lost,
}

public sealed class MicrophoneException : Exception
{
    public MicrophoneException()
    {
    }

    public MicrophoneException(string message)
        : base(message)
    {
    }

    public MicrophoneException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MicrophoneException(MicrophoneProblem problem, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Problem = problem;
    }

    public MicrophoneProblem Problem { get; }
}
