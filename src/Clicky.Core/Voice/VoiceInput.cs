using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Voice;

public enum VoiceOutcome
{
    /// <summary><see cref="VoiceResult.Text"/> holds what was said.</summary>
    Transcribed,

    /// <summary>Too little speech (a tap, a silent hold), or only text Whisper invents from noise. Nothing to do.</summary>
    NoSpeech,

    /// <summary>The session was cancelled before it completed.</summary>
    Cancelled,

    /// <summary>The mic or the speech engine failed; <see cref="VoiceResult.Problem"/> says why, in words for the user.</summary>
    Failed,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Text">Cleaned transcript; empty unless <see cref="VoiceOutcome.Transcribed"/>.</param>
/// <param name="Problem">User-facing reason for <see cref="VoiceOutcome.Failed"/>.</param>
/// <param name="MicrophoneProblem">Set when the microphone was the cause of a failure.</param>
/// <param name="Recorded">Length of the recording.</param>
/// <param name="Analysis">What the speech gate found.</param>
/// <param name="Transcript">Raw engine result, when the engine ran.</param>
/// <param name="ReleaseToText">From <see cref="VoiceSession.CompleteAsync"/> being called (key release) to the result.</param>
public sealed record VoiceResult(
    VoiceOutcome Outcome,
    string Text,
    string? Problem,
    MicrophoneProblem? MicrophoneProblem,
    TimeSpan Recorded,
    SpeechAnalysis Analysis,
    Transcript? Transcript,
    TimeSpan ReleaseToText);

/// <summary>
/// Push-to-talk speech input: open the mic, record, then gate, transcribe and clean. Holds no
/// state between sessions, so a new Talk can start while the previous one is still transcribing.
/// </summary>
public sealed class VoiceInput
{
    public VoiceInput(
        IMicrophone microphone,
        Func<SpeechEngine, ISpeechToText> engines,
        ISettingsStore settings,
        ClickyPaths paths,
        ILogger<VoiceInput> logger,
        TimeProvider? time = null)
    {
        Microphone = microphone;
        Engines = engines;
        Settings = settings;
        Paths = paths;
        Logger = logger;
        Time = time ?? TimeProvider.System;
    }

    internal IMicrophone Microphone { get; }

    internal Func<SpeechEngine, ISpeechToText> Engines { get; }

    internal ISettingsStore Settings { get; }

    internal ClickyPaths Paths { get; }

    internal ILogger Logger { get; }

    internal TimeProvider Time { get; }

    /// <summary>
    /// Starts opening the mic and returns at once; audio is kept from the moment the device starts.
    /// Call this as early as possible (key down), and <see cref="VoiceSession.Cancel"/> if it turns
    /// out not to be a Talk after all.
    /// </summary>
    public VoiceSession Start() => new(this, Settings.Current);

    /// <summary>Gets the configured speech engine ready (model download and load). Never throws.</summary>
    public Task WarmUpAsync(CancellationToken cancellationToken = default) =>
        Engines(Settings.Current.Voice.Engine).WarmUpAsync(cancellationToken);
}
/// <summary>One Talk: from key down to transcript.</summary>
public sealed class VoiceSession : IDisposable
{
    private readonly VoiceInput _owner;
    private readonly ClickySettings _settings;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task<IMicrophoneRecording> _recording;
    private readonly long _startedAt;
    private SpeechAudioSink? _sink;
    private int _finished;

    internal VoiceSession(VoiceInput owner, ClickySettings settings)
    {
        _owner = owner;
        _settings = settings;
        _startedAt = owner.Time.GetTimestamp();
        var voice = settings.Voice;
        owner.Engines(voice.Engine).Prepare();
        var maxDuration = TimeSpan.FromSeconds(voice.MaxRecordingSeconds);
        _recording = Task.Run(
            () => owner.Microphone.StartAsync(
                voice.InputDevice,
                format =>
                {
                    var sink = new SpeechAudioSink(format, maxDuration);
                    sink.LevelChanged += level => LevelChanged?.Invoke(level);
                    _sink = sink;
                    return sink;
                },
                _cancel.Token),
            CancellationToken.None);
    }

    /// <summary>Input level 0..1, raised on the capture thread about every 50 ms.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>Stops recording and throws the audio away. Safe to call at any time, more than once.</summary>
    public void Cancel()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        _cancel.Cancel();
        _ = DiscardAsync();
    }

    /// <summary>Stops recording (the key was released) and turns the audio into text.</summary>
    public async Task<VoiceResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        var released = _owner.Time.GetTimestamp();
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return Result(VoiceOutcome.Cancelled, released);
        }

        var logger = _owner.Logger;
        IMicrophoneRecording recording;
        try
        {
            recording = await _recording.ConfigureAwait(false);
        }
        catch (MicrophoneException ex)
        {
            logger.LogWarning(ex, "Talk: microphone failed to start ({Problem})", ex.Problem);
            return Result(VoiceOutcome.Failed, released, problem: ex.Message, micProblem: ex.Problem);
        }
        finally
        {
            _cancel.Dispose();
        }

        MicrophoneException? stopProblem;
        await using (recording.ConfigureAwait(false))
        {
            stopProblem = await recording.StopAsync().ConfigureAwait(false);
        }

        var sink = _sink;
        var samples = sink?.Complete() ?? [];
        var recorded = SpeechAudio.DurationOf(samples.Length);
        if (sink?.Truncated == true)
        {
            logger.LogWarning("Talk: recording reached {Seconds} s and was cut off there", _settings.Voice.MaxRecordingSeconds);
        }

        var analysis = SpeechDetector.Analyze(samples);
        SaveDebugAudio(samples);

        if (stopProblem is not null)
        {
            logger.LogWarning(stopProblem, "Talk: microphone {Device} stopped early after {Seconds:0.0} s ({Problem})", recording.DeviceName, recorded.TotalSeconds, stopProblem.Problem);
            if (!analysis.HasSpeech(MinSpeech))
            {
                return Result(VoiceOutcome.Failed, released, recorded, analysis, problem: stopProblem.Message, micProblem: stopProblem.Problem);
            }
        }

        if (!analysis.HasSpeech(MinSpeech))
        {
            logger.LogInformation("Talk: no speech in {Seconds:0.00} s from {Device} ({Analysis})", recorded.TotalSeconds, recording.DeviceName, analysis);
            return Result(VoiceOutcome.NoSpeech, released, recorded, analysis);
        }

        var voice = _settings.Voice;
        var engine = _owner.Engines(voice.Engine);
        Transcript transcript;
        try
        {
            var speech = samples.AsMemory(analysis.Start, analysis.Length);
            transcript = await engine.TranscribeAsync(speech, voice.Language, cancellationToken).ConfigureAwait(false);
        }
        catch (SpeechToTextException ex)
        {
            logger.LogWarning(ex, "Talk: speech-to-text failed ({Engine})", engine.Description);
            return Result(VoiceOutcome.Failed, released, recorded, analysis, problem: ex.Message);
        }

        var text = TranscriptCleaner.Clean(transcript.Text, analysis.SpeechDuration);
        var elapsed = _owner.Time.GetElapsedTime(released);
        if (text.Length == 0)
        {
            logger.LogInformation(
                "Talk: dropped \"{Raw}\" from {Engine} as non-speech ({Analysis})",
                transcript.Text,
                transcript.Engine,
                analysis);
            return Result(VoiceOutcome.NoSpeech, released, recorded, analysis, transcript: transcript);
        }

        logger.LogInformation(
            "Talk: \"{Text}\" ({Seconds:0.0} s from {Device}, {Analysis}; {Engine} {TranscribeMs:0} ms, release to text {ReleaseMs:0} ms)",
            text,
            recorded.TotalSeconds,
            recording.DeviceName,
            analysis,
            transcript.Engine,
            transcript.Elapsed.TotalMilliseconds,
            elapsed.TotalMilliseconds);
        return new VoiceResult(VoiceOutcome.Transcribed, text, null, null, recorded, analysis, transcript, elapsed);
    }

    /// <summary>Same as <see cref="Cancel"/> unless the session already completed.</summary>
    public void Dispose() => Cancel();

    private TimeSpan MinSpeech => TimeSpan.FromMilliseconds(_settings.Voice.MinSpeechMs);

    private VoiceResult Result(
        VoiceOutcome outcome,
        long released,
        TimeSpan recorded = default,
        SpeechAnalysis analysis = default,
        string? problem = null,
        MicrophoneProblem? micProblem = null,
        Transcript? transcript = null) =>
        new(outcome, string.Empty, problem, micProblem, recorded, analysis, transcript, _owner.Time.GetElapsedTime(released));

    private async Task DiscardAsync()
    {
        try
        {
            var recording = await _recording.ConfigureAwait(false);
            await recording.DisposeAsync().ConfigureAwait(false);
            _owner.Logger.LogDebug("Talk: recording discarded after {Ms:0} ms", _owner.Time.GetElapsedTime(_startedAt).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is MicrophoneException or OperationCanceledException)
        {
            _owner.Logger.LogDebug("Talk: cancelled before the microphone started ({Message})", ex.Message);
        }
        finally
        {
            _cancel.Dispose();
        }
    }

    private void SaveDebugAudio(float[] samples)
    {
        if (!_settings.Debug.SaveAudio || samples.Length == 0)
        {
            return;
        }

        try
        {
            var directory = _owner.Paths.DebugAudioDirectory;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{_owner.Time.GetLocalNow():yyyyMMdd-HHmmss}.wav");
            File.WriteAllBytes(path, WavEncoder.EncodePcm16(samples));
            _owner.Logger.LogInformation("Talk: saved recording to {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _owner.Logger.LogWarning(ex, "Talk: could not save the debug recording");
        }
    }
}
