using System.Threading.Channels;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Dictation;

/// <summary>How a dictation was started.</summary>
public enum DictationMode
{
    /// <summary>While the keys are held (hold Ctrl+Win). Text goes in on release.</summary>
    Hold,

    /// <summary>Until the gesture is repeated (double-tap Ctrl+Win). Text can go in while speaking.</summary>
    HandsFree,
}

/// <summary>One transcribed chunk of a dictation, already cleaned and run through the dictionary.</summary>
public sealed record DictationPiece(int Index, string Text, TimeSpan Offset, TimeSpan Duration);

/// <param name="Text">Everything that was said, joined; may be non-empty even when <paramref name="Problem"/> is set.</param>
/// <param name="Problem">User-facing reason the dictation ended early or lost something, if it did.</param>
/// <param name="MicrophoneProblem">Set when the microphone was the cause.</param>
/// <param name="Recorded">How long the microphone ran.</param>
/// <param name="ReleaseToText">From <see cref="DictationSession.CompleteAsync"/> to the last piece.</param>
public sealed record DictationResult(
    string Text,
    IReadOnlyList<DictationPiece> Pieces,
    string? Problem,
    MicrophoneProblem? MicrophoneProblem,
    TimeSpan Recorded,
    TimeSpan ReleaseToText);

/// <summary>The cleanup pass on dictated text (punctuation and grammar, never new words). Runs per piece.</summary>
public interface IDictationPolisher
{
    Task<string> PolishAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Until a fast model is wired in (M5), the cleanup is the rules in <see cref="DictationText"/> and
/// the speech engine's own punctuation.
/// </summary>
public sealed class RulesOnlyPolisher : IDictationPolisher
{
    public Task<string> PolishAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(text);
}

/// <summary>
/// Dictation speech input: opens the mic, cuts the stream into chunks at pauses and transcribes
/// each one while the user keeps talking. Every piece is written to the history as soon as it
/// exists. Holds no state between sessions.
/// </summary>
public sealed class DictationInput(
    IMicrophone microphone,
    Func<SpeechEngine, ISpeechToText> engines,
    ISettingsStore settings,
    PersonalVocabulary dictionary,
    DictationHistory history,
    IDictationPolisher polisher,
    ILogger<DictationInput> logger,
    TimeProvider? time = null)
{
    internal IMicrophone Microphone => microphone;

    internal Func<SpeechEngine, ISpeechToText> Engines => engines;

    internal PersonalVocabulary Dictionary => dictionary;

    internal DictationHistory History => history;

    internal IDictationPolisher Polisher => polisher;

    internal ILogger Logger => logger;

    internal TimeProvider Time { get; } = time ?? TimeProvider.System;

    /// <summary>Starts opening the mic and returns at once. Call as early as possible (key down).</summary>
    public DictationSession Start(DictationMode mode) => new(this, settings.Current, mode);
}

/// <summary>One dictation, from key down to the last transcribed piece.</summary>
/// <remarks>
/// Audio arrives on the capture thread and is cut there (cheap); chunks are transcribed one at a
/// time on a worker, in order. <see cref="PieceReady"/> is raised on that worker.
/// </remarks>
public sealed class DictationSession : IDisposable
{
    private readonly DictationInput _owner;
    private readonly ClickySettings _settings;
    private readonly DictationSegmenter _segmenter = new();
    private readonly Lock _segmenterGate = new();
    private readonly Channel<DictationChunk> _chunks = Channel.CreateUnbounded<DictationChunk>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task<IMicrophoneRecording> _recording;
    private readonly List<DictationPiece> _pieces = [];
    private readonly string _prompt;
    private readonly long _startedAt;
    private Task? _worker;
    private SpeechAudioSink? _sink;
    private string? _problem;
    private int _limitRaised;
    private int _finished;
    private bool _historyStarted;

    internal DictationSession(DictationInput owner, ClickySettings settings, DictationMode mode)
    {
        _owner = owner;
        _settings = settings;
        Mode = mode;
        _startedAt = owner.Time.GetTimestamp();
        _prompt = owner.Dictionary.BuildPrompt();
        owner.Engines(settings.Voice.Engine).Prepare();
        var maxDuration = settings.Dictation.MaxDuration;
        _recording = Task.Run(
            () => owner.Microphone.StartAsync(
                settings.Voice.InputDevice,
                format =>
                {
                    var sink = new SpeechAudioSink(format, maxDuration, OnAudio);
                    sink.LevelChanged += level => LevelChanged?.Invoke(level);
                    _sink = sink;
                    return sink;
                },
                _cancel.Token),
            CancellationToken.None);
        Opened = OpenedAsync();
    }

    public DictationMode Mode { get; }

    /// <summary>Input level 0..1, raised on the capture thread about every 50 ms.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>A chunk was transcribed. Raised in order, on the transcription worker.</summary>
    public event Action<DictationPiece>? PieceReady;

    /// <summary>The recording reached settings.dictation.maxMinutes; complete the session. Raised once, on the thread pool.</summary>
    public event Action? LimitReached;

    /// <summary>Completes when the mic is recording, with the reason it could not start, or null.</summary>
    public Task<MicrophoneException?> Opened { get; }

    /// <summary>Text transcribed so far.</summary>
    public string TextSoFar
    {
        get
        {
            lock (_pieces)
            {
                return Joined(_pieces);
            }
        }
    }

    /// <summary>Stops recording and throws away audio not yet transcribed. Pieces already in the history stay there.</summary>
    public void Cancel()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        _cancel.Cancel();
        _chunks.Writer.TryComplete();
        _ = DiscardAsync();
    }

    /// <summary>Stops recording and returns everything said, once the last chunk is transcribed.</summary>
    public async Task<DictationResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        var released = _owner.Time.GetTimestamp();
        var logger = _owner.Logger;
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return Result(released, TimeSpan.Zero, null);
        }

        IMicrophoneRecording recording;
        try
        {
            recording = await _recording.ConfigureAwait(false);
        }
        catch (MicrophoneException ex)
        {
            logger.LogWarning(ex, "Dictation: microphone failed to start ({Problem})", ex.Problem);
            _chunks.Writer.TryComplete();
            return new DictationResult(string.Empty, [], ex.Message, ex.Problem, TimeSpan.Zero, _owner.Time.GetElapsedTime(released));
        }

        MicrophoneException? stopProblem;
        await using (recording.ConfigureAwait(false))
        {
            stopProblem = await recording.StopAsync().ConfigureAwait(false);
        }

        _sink?.Complete();
        lock (_segmenterGate)
        {
            if (_segmenter.Flush() is { } last)
            {
                Enqueue(last);
            }
        }

        _chunks.Writer.TryComplete();
        var recorded = SpeechAudio.DurationOf(_sink?.SampleCount ?? 0);
        if (_worker is { } worker)
        {
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (stopProblem is not null)
        {
            logger.LogWarning(stopProblem, "Dictation: microphone {Device} stopped early after {Seconds:0.0} s ({Problem})", recording.DeviceName, recorded.TotalSeconds, stopProblem.Problem);
            _problem ??= stopProblem.Message;
        }

        var result = Result(released, recorded, stopProblem?.Problem);
        logger.LogInformation(
            "Dictation ({Mode}): {Chars} chars in {Pieces} pieces from {Seconds:0.0} s on {Device}; release to text {ReleaseMs:0} ms",
            Mode,
            result.Text.Length,
            result.Pieces.Count,
            recorded.TotalSeconds,
            recording.DeviceName,
            result.ReleaseToText.TotalMilliseconds);
        return result;
    }

    /// <summary>Same as <see cref="Cancel"/> unless the session already completed.</summary>
    public void Dispose() => Cancel();

    private DictationResult Result(long released, TimeSpan recorded, MicrophoneProblem? micProblem)
    {
        DictationPiece[] pieces;
        lock (_pieces)
        {
            pieces = [.. _pieces];
        }

        return new DictationResult(Joined(pieces), pieces, _problem, micProblem, recorded, _owner.Time.GetElapsedTime(released));
    }

    private static string Joined(IEnumerable<DictationPiece> pieces) =>
        pieces.Aggregate(string.Empty, (text, piece) => DictationText.Join(text, piece.Text));

    private async Task<MicrophoneException?> OpenedAsync()
    {
        try
        {
            await _recording.ConfigureAwait(false);
            return null;
        }
        catch (MicrophoneException ex)
        {
            return ex;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // Capture thread.
    private void OnAudio(ReadOnlySpan<float> samples)
    {
        lock (_segmenterGate)
        {
            foreach (var chunk in _segmenter.Append(samples))
            {
                Enqueue(chunk);
            }
        }

        if (_sink is { Truncated: true } && Interlocked.Exchange(ref _limitRaised, 1) == 0)
        {
            _owner.Logger.LogWarning("Dictation: reached {Minutes} min and stopped listening", _settings.Dictation.MaxMinutes);
            ThreadPool.QueueUserWorkItem(_ => LimitReached?.Invoke());
        }
    }

    private void Enqueue(DictationChunk chunk)
    {
        _chunks.Writer.TryWrite(chunk);
        _worker ??= Task.Run(ProcessAsync);
    }

    private async Task ProcessAsync()
    {
        var index = 0;
        await foreach (var chunk in _chunks.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (_cancel.IsCancellationRequested)
            {
                continue;
            }

            try
            {
                if (await TranscribeAsync(chunk, index).ConfigureAwait(false) is { } piece)
                {
                    index++;
                    lock (_pieces)
                    {
                        _pieces.Add(piece);
                    }

                    PieceReady?.Invoke(piece);
                }
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One bad chunk must not take the rest of the dictation with it.
                _owner.Logger.LogWarning(ex, "Dictation: chunk at {Offset:0.0} s failed", chunk.Offset.TotalSeconds);
                _problem ??= ex is SpeechToTextException ? ex.Message : "Part of the dictation could not be transcribed.";
            }
        }
    }

    private async Task<DictationPiece?> TranscribeAsync(DictationChunk chunk, int index)
    {
        var voice = _settings.Voice;
        var analysis = SpeechDetector.Analyze(chunk.Samples);
        if (!analysis.HasSpeech(TimeSpan.FromMilliseconds(voice.MinSpeechMs)))
        {
            _owner.Logger.LogDebug("Dictation: no speech in {Seconds:0.0} s at {Offset:0.0} s ({Analysis})", chunk.Duration.TotalSeconds, chunk.Offset.TotalSeconds, analysis);
            return null;
        }

        var engine = _owner.Engines(voice.Engine);
        var speech = chunk.Samples.AsMemory(analysis.Start, analysis.Length);
        var transcript = await engine.TranscribeAsync(speech, voice.Language, _prompt, _cancel.Token).ConfigureAwait(false);
        var text = TranscriptCleaner.Clean(transcript.Text, analysis.SpeechDuration);
        if (text.Length == 0 || EchoesPrompt(text))
        {
            _owner.Logger.LogDebug("Dictation: dropped \"{Raw}\" at {Offset:0.0} s as non-speech", transcript.Text, chunk.Offset.TotalSeconds);
            return null;
        }

        text = _owner.Dictionary.Apply(text);
        text = DictationText.RemoveDashes(text);
        if (_settings.Dictation.SpokenCommands)
        {
            text = DictationText.ApplySpokenCommands(text);
        }

        text = await _owner.Polisher.PolishAsync(text, _cancel.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text) && !text.Contains('\n', StringComparison.Ordinal))
        {
            return null;
        }

        var keepDays = _settings.Dictation.KeepHistoryDays;
        if (!_historyStarted)
        {
            _historyStarted = true;
            _owner.History.BeginSession(Mode == DictationMode.Hold ? "dictation" : "hands-free dictation", keepDays);
        }

        _owner.History.Add(text, keepDays);
        _owner.Logger.LogInformation(
            "Dictation piece {Index}: {Chars} chars from {Seconds:0.0} s at {Offset:0.0} s ({Engine} {Ms:0} ms)",
            index,
            text.Length,
            chunk.Duration.TotalSeconds,
            chunk.Offset.TotalSeconds,
            transcript.Engine,
            transcript.Elapsed.TotalMilliseconds);
        _owner.Logger.LogDebug("Dictation piece {Index}: \"{Text}\"", index, text);
        return new DictationPiece(index, text, chunk.Offset, chunk.Duration);
    }

    /// <summary>On near-silence Whisper sometimes just repeats its prompt (here, the dictionary).</summary>
    private bool EchoesPrompt(string text)
    {
        if (_prompt.Length == 0)
        {
            return false;
        }

        var key = TranscriptCleaner.Key(text);
        return key.Length > 0 && TranscriptCleaner.Key(_prompt).Contains(key, StringComparison.Ordinal) && key.Contains(' ', StringComparison.Ordinal);
    }

    private async Task DiscardAsync()
    {
        try
        {
            var recording = await _recording.ConfigureAwait(false);
            await recording.DisposeAsync().ConfigureAwait(false);
            _owner.Logger.LogDebug("Dictation: discarded after {Ms:0} ms", _owner.Time.GetElapsedTime(_startedAt).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is MicrophoneException or OperationCanceledException)
        {
            _owner.Logger.LogDebug("Dictation: cancelled before the microphone started ({Message})", ex.Message);
        }
    }
}
