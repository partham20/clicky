namespace Clicky.Core.Voice;

/// <summary>The system audio output. Each reply opens the device fresh, so it follows default-device changes.</summary>
public interface ISpeaker
{
    /// <summary>
    /// Opens the output device in its own shared-mode format and starts playing <see cref="ISpeakerSession.Buffer"/>
    /// (silence until something is written). Returns once the device is running.
    /// </summary>
    /// <param name="deviceId">A device id, or empty for the Windows default output.</param>
    /// <param name="gain">Linear volume 0..1 applied on top of the Windows volume.</param>
    /// <exception cref="SpeakerException">No usable device, or it failed to start.</exception>
    Task<ISpeakerSession> OpenAsync(string deviceId, float gain, CancellationToken cancellationToken = default);
}

/// <summary>A running output device. Dispose stops the device at once and releases it.</summary>
public interface ISpeakerSession : IAsyncDisposable
{
    /// <summary>Friendly device name, for logs.</summary>
    string DeviceName { get; }

    /// <summary>Mono samples at the device rate go here.</summary>
    SpeakerBuffer Buffer { get; }

    /// <summary>Completes with null when the device was stopped normally, or with why it failed (unplugged, …).</summary>
    Task<SpeakerException?> Stopped { get; }
}

public enum SpeakerProblem
{
    Failed,

    /// <summary>No output device is plugged in or enabled.</summary>
    NoDevice,

    /// <summary>The device was unplugged or disabled while playing.</summary>
    Lost,

    /// <summary>Another app has the device in exclusive mode.</summary>
    InUse,
}

public sealed class SpeakerException : Exception
{
    public SpeakerException()
    {
    }

    public SpeakerException(string message)
        : base(message)
    {
    }

    public SpeakerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SpeakerException(SpeakerProblem problem, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Problem = problem;
    }

    public SpeakerProblem Problem { get; }
}
