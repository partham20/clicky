using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Voice;

public enum ReplyOutcome
{
    /// <summary>Spoken to the end.</summary>
    Spoken,

    /// <summary>Not spoken because of <see cref="ReplyResult.Quiet"/>; show <see cref="ReplyResult.Text"/> instead.</summary>
    ShownAsText,

    /// <summary>Stopped early: barge-in, a newer reply, or shutdown.</summary>
    Interrupted,

    /// <summary>The voice or the speakers failed; <see cref="ReplyResult.Problem"/> says why. Show the text instead.</summary>
    Failed,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Text">The whole reply as it was given, for a text card.</param>
/// <param name="Quiet">Why it was not spoken, for <see cref="ReplyOutcome.ShownAsText"/>.</param>
/// <param name="Problem">One line for the user: the quiet reason or the failure.</param>
/// <param name="Sentences">Sentences spoken (or that would have been).</param>
/// <param name="FirstAudio">From the first complete sentence to its first sound; zero if nothing played.</param>
public sealed record ReplyResult(ReplyOutcome Outcome, string Text, QuietReasons Quiet, string? Problem, int Sentences, TimeSpan FirstAudio);

/// <summary>
/// Speaks replies sentence by sentence as their text streams in: each sentence is synthesized as
/// soon as it is complete, the next while the current one plays, and they are queued back to back
/// on the speaker. One reply at a time; starting another, or <see cref="Interrupt"/> (barge-in),
/// fades the current one out.
/// </summary>
/// <remarks>
/// Before speaking, the PC is checked for reasons to stay quiet (<see cref="IQuietDetector"/>,
/// <see cref="QuietPolicy"/>); a quiet reply only collects its text so it can be shown instead.
/// </remarks>
public sealed class VoiceOutput : IDisposable
{
    /// <summary>Pause between two sentences; synthesized clips have their own silence trimmed off.</summary>
    public static readonly TimeSpan SentenceGap = TimeSpan.FromMilliseconds(180);

    private readonly Lock _gate = new();
    private SpokenReply? _current;

    // A speaker opened ahead of a reply (see Prepare), and which device setting it was opened for.
    private Task<ISpeakerSession>? _warm;
    private string? _warmDevice;

    public VoiceOutput(
        ISpeaker speaker,
        Func<VoiceOutputEngine, ITextToSpeech> engines,
        IQuietDetector quiet,
        ISettingsStore settings,
        ILogger<VoiceOutput> logger,
        TimeProvider? time = null)
    {
        Speaker = speaker;
        Engines = engines;
        Quiet = quiet;
        Settings = settings;
        Logger = logger;
        Time = time ?? TimeProvider.System;
    }

    internal ISpeaker Speaker { get; }

    internal Func<VoiceOutputEngine, ITextToSpeech> Engines { get; }

    internal IQuietDetector Quiet { get; }

    internal ISettingsStore Settings { get; }

    internal ILogger Logger { get; }

    internal TimeProvider Time { get; }

    /// <summary>How long a speaker opened by <see cref="Prepare"/> waits for a reply before it is closed.</summary>
    internal TimeSpan WarmLifetime { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>True while a reply is being spoken.</summary>
    public bool IsSpeaking
    {
        get
        {
            lock (_gate)
            {
                return _current is { IsSpeaking: true, Completion.IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// Starts a reply: decides now whether to speak or stay quiet, and if speaking, opens the
    /// speaker while the first sentence is still arriving. Interrupts any reply still playing.
    /// </summary>
    public SpokenReply Begin()
    {
        var settings = Settings.Current.Speech;
        var check = Quiet.Check(settings);
        var quiet = QuietPolicy.Apply(settings, check.Reasons);
        if (check.Reasons != QuietReasons.None)
        {
            Logger.LogInformation("Speech: detected {Detected}{Detail}; staying quiet for {Quiet}", check.Reasons, check.Detail is null ? string.Empty : $" ({check.Detail})", quiet);
        }

        var reply = new SpokenReply(this, settings, quiet, check.Detail);
        SpokenReply? previous;
        lock (_gate)
        {
            previous = _current;
            _current = reply;
        }

        previous?.Interrupt();
        reply.Start();
        return reply;
    }

    /// <summary>Speaks a whole text as one reply.</summary>
    public SpokenReply Speak(string text)
    {
        var reply = Begin();
        reply.Append(text);
        reply.Complete();
        return reply;
    }

    /// <summary>Barge-in: stops whatever is being spoken. Returns true if something was.</summary>
    public bool Interrupt()
    {
        SpokenReply? current;
        lock (_gate)
        {
            current = _current;
        }

        return current?.Interrupt() == true;
    }

    /// <summary>
    /// A reply is coming soon (the user just asked something): open the speaker now. Opening a
    /// WASAPI stream costs 250 ms to 1 s on common laptop drivers, which would otherwise come
    /// straight out of the time to first sound. The next reply within <see cref="WarmLifetime"/>
    /// uses it; after that it is closed. Returns at once; never throws.
    /// </summary>
    public void Prepare()
    {
        var settings = Settings.Current.Speech;
        if (settings.QuietMode)
        {
            return;
        }

        Task<ISpeakerSession> warm;
        lock (_gate)
        {
            if (_warm is not null)
            {
                return;
            }

            warm = Task.Run(() => Speaker.OpenAsync(settings.OutputDevice, (float)settings.Volume), CancellationToken.None);
            _warm = warm;
            _warmDevice = settings.OutputDevice;
        }

        _ = ExpireAsync(warm);
    }

    /// <summary>Gets the configured voice ready. Never throws.</summary>
    public Task WarmUpAsync(CancellationToken cancellationToken = default) =>
        Engines(Settings.Current.Speech.Engine).WarmUpAsync(cancellationToken);

    public void Dispose()
    {
        Interrupt();
        Task<ISpeakerSession>? warm;
        lock (_gate)
        {
            warm = _warm;
            _warm = null;
        }

        if (warm is not null)
        {
            _ = CloseAsync(warm);
        }
    }

    /// <summary>The speaker for a reply: the prepared one if it suits, else a fresh one.</summary>
    internal async Task<ISpeakerSession> OpenSpeakerAsync(SpeechSettings settings, CancellationToken cancellationToken)
    {
        Task<ISpeakerSession>? warm = null;
        lock (_gate)
        {
            if (_warm is not null && string.Equals(_warmDevice, settings.OutputDevice, StringComparison.Ordinal))
            {
                warm = _warm;
                _warm = null;
            }
        }

        if (warm is not null)
        {
            try
            {
                var session = await warm.ConfigureAwait(false);
                session.Buffer.Gain = (float)settings.Volume;
                return session;
            }
            catch (SpeakerException ex)
            {
                Logger.LogDebug(ex, "Speech: the prepared speaker failed to open; trying again");
            }
        }

        return await Speaker.OpenAsync(settings.OutputDevice, (float)settings.Volume, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CloseAsync(Task<ISpeakerSession> warm)
    {
        try
        {
            var session = await warm.ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SpeakerException or OperationCanceledException)
        {
        }
    }

    private async Task ExpireAsync(Task<ISpeakerSession> warm)
    {
        await Task.Delay(WarmLifetime, Time).ConfigureAwait(false);
        lock (_gate)
        {
            if (!ReferenceEquals(_warm, warm))
            {
                return;
            }

            _warm = null;
        }

        Logger.LogDebug("Speech: closing the prepared speaker, no reply came");
        await CloseAsync(warm).ConfigureAwait(false);
    }
}

/// <summary>One reply: text in, speech (or a quiet decision) out.</summary>
public sealed class SpokenReply : IDisposable
{
    private readonly VoiceOutput _owner;
    private readonly SpeechSettings _settings;
    private readonly string? _quietDetail;
    private readonly Lock _gate = new();
    private readonly StringBuilder _text = new();
    private readonly SentenceSplitter _splitter = new();
    private readonly Channel<string> _sentences = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource<ReplyResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _begunAt;

    private ISpeakerSession? _stream;
    private int _sentenceCount;
    private long _firstSentenceAt;
    private bool _completed;
    private int _interrupted;

    internal SpokenReply(VoiceOutput owner, SpeechSettings settings, QuietReasons quiet, string? quietDetail)
    {
        _owner = owner;
        _settings = settings;
        _quietDetail = quietDetail;
        Quiet = quiet;
        _begunAt = owner.Time.GetTimestamp();
    }

    /// <summary>A sentence (as spoken, cleaned) started playing. Raised on a pool or audio thread.</summary>
    public event Action<string>? SentenceStarted;

    /// <summary>Output level 0..1 while speaking, about 20 times a second. Raised on the audio thread.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>Why this reply is not spoken; None if it is.</summary>
    public QuietReasons Quiet { get; }

    public bool IsSpeaking => Quiet == QuietReasons.None;

    /// <summary>Completes when the reply has been spoken, stopped, failed, or (quiet) completed.</summary>
    public Task<ReplyResult> Completion => _completion.Task;

    /// <summary>The reply text so far, as given.</summary>
    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }

    /// <summary>More reply text (any size: a token, a sentence, everything).</summary>
    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _text.Append(text);
            Queue(_splitter.Append(text));
        }
    }

    /// <summary>No more text is coming.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            Queue(_splitter.Complete());
        }

        _sentences.Writer.TryComplete();
        if (!IsSpeaking)
        {
            var text = Text;
            _owner.Logger.LogInformation("Speech: reply of {Chars} chars shown as text ({Quiet})", text.Length, Quiet);
            _completion.TrySetResult(new ReplyResult(ReplyOutcome.ShownAsText, text, Quiet, QuietPolicy.Describe(Quiet, _quietDetail), _sentenceCount, TimeSpan.Zero));
        }
    }

    /// <summary>Stops speaking: fades out what is playing and drops the rest. Returns true if it was still going.</summary>
    public bool Interrupt()
    {
        if (Interlocked.Exchange(ref _interrupted, 1) != 0 || _completion.Task.IsCompleted)
        {
            return false;
        }

        lock (_gate)
        {
            _completed = true;
        }

        _sentences.Writer.TryComplete();
        Volatile.Read(ref _stream)?.Buffer.Stop();
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (!IsSpeaking)
        {
            _completion.TrySetResult(new ReplyResult(ReplyOutcome.Interrupted, Text, Quiet, null, _sentenceCount, TimeSpan.Zero));
        }

        return true;
    }

    public void Dispose() => Interrupt();

    internal void Start()
    {
        if (IsSpeaking)
        {
            _ = Task.Run(RunAsync, CancellationToken.None);
        }
    }

    private void Queue(IReadOnlyList<string> sentences)
    {
        foreach (var raw in sentences)
        {
            var spoken = SpeakableText.Clean(raw);
            if (spoken.Length == 0)
            {
                continue;
            }

            if (_sentenceCount++ == 0)
            {
                _firstSentenceAt = _owner.Time.GetTimestamp();
            }

            _sentences.Writer.TryWrite(spoken);
        }
    }

    private async Task RunAsync()
    {
        var logger = _owner.Logger;
        var token = _cancel.Token;
        var engine = _owner.Engines(_settings.Engine);
        var request = new SpeechRequest(_settings.Engine == VoiceOutputEngine.Cloud ? _settings.CloudVoice : _settings.WindowsVoice, _settings.Rate);

        // Open the device (unless Prepare already did) while the first sentence is still being written and synthesized.
        var opening = Task.Run(() => _owner.OpenSpeakerAsync(_settings, token), CancellationToken.None);
        // Look-ahead: one sentence playing, one queued behind it, one ready and one being synthesized.
        // Enough to keep the voice going, little enough that barge-in wastes almost nothing.
        var clips = Channel.CreateBounded<(string Text, SpeechClip Clip)>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
        var synthesis = Task.Run(() => SynthesizeAsync(engine, request, clips.Writer, token), CancellationToken.None);

        ReplyResult result;
        var firstAudio = TimeSpan.Zero;
        var synthTime = TimeSpan.Zero;
        var played = 0;
        try
        {
            var stream = await opening.ConfigureAwait(false);
            Volatile.Write(ref _stream, stream);
            stream.Buffer.LevelChanged += OnLevel;
            if (Volatile.Read(ref _interrupted) != 0)
            {
                stream.Buffer.Stop();
            }

            var buffer = stream.Buffer;
            var previousStart = 0L;
            await foreach (var (text, clip) in clips.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                // Queue this sentence once the one before it has started playing, not before.
                if (played > 0)
                {
                    await buffer.WhenPlayed(previousStart + 1).WaitAsync(token).ConfigureAwait(false);
                }

                synthTime += clip.Elapsed;
                var samples = Resample(clip, buffer.SampleRate);
                if (played > 0)
                {
                    buffer.WriteSilence(VoiceOutput.SentenceGap);
                }

                var start = buffer.Written;
                previousStart = start;
                buffer.Write(samples);
                var first = played++ == 0;
                _ = buffer.WhenPlayed(start + 1).ContinueWith(
                    _ =>
                    {
                        if (buffer.Played > start && Volatile.Read(ref _interrupted) == 0)
                        {
                            if (first)
                            {
                                firstAudio = _owner.Time.GetElapsedTime(_firstSentenceAt);
                            }

                            SentenceStarted?.Invoke(text);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            await synthesis.ConfigureAwait(false);
            buffer.Complete();
            var stopped = await Task.WhenAny(buffer.Drained, stream.Stopped).ConfigureAwait(false);
            if (stopped == stream.Stopped && await stream.Stopped.ConfigureAwait(false) is { } lost)
            {
                throw lost;
            }

            var interrupted = Volatile.Read(ref _interrupted) != 0;
            result = new ReplyResult(interrupted ? ReplyOutcome.Interrupted : ReplyOutcome.Spoken, Text, QuietReasons.None, null, _sentenceCount, firstAudio);
            logger.LogInformation(
                "Speech: {Outcome} {Sentences} sentence(s) on {Device} with {Engine}; first audio {FirstMs:0} ms after the first sentence, synthesis {SynthMs:0} ms total, gaps {GapMs:0} ms",
                result.Outcome,
                played,
                stream.DeviceName,
                engine.Description,
                firstAudio.TotalMilliseconds,
                synthTime.TotalMilliseconds,
                buffer.Underruns.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _interrupted) != 0)
        {
            result = new ReplyResult(ReplyOutcome.Interrupted, Text, QuietReasons.None, null, _sentenceCount, firstAudio);
            logger.LogInformation("Speech: interrupted after {Played} of {Sentences} sentence(s)", played, _sentenceCount);
        }
        catch (Exception ex) when (ex is TextToSpeechException or SpeakerException)
        {
            var problem = ex is SpeakerException { Problem: SpeakerProblem.NoDevice }
                ? QuietPolicy.Describe(QuietReasons.NoOutputDevice)
                : ex.Message;
            logger.LogWarning(ex, "Speech: {Engine} could not speak the reply ({Problem})", engine.Description, problem);
            result = new ReplyResult(ReplyOutcome.Failed, Text, QuietReasons.None, problem, _sentenceCount, firstAudio);
        }
        catch (Exception ex)
        {
            // Never leave a reply hanging: whatever went wrong, the text can still be shown.
            logger.LogError(ex, "Speech: unexpected failure");
            result = new ReplyResult(ReplyOutcome.Failed, Text, QuietReasons.None, "Something went wrong with the voice.", _sentenceCount, firstAudio);
        }
        finally
        {
            await _cancel.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(synthesis, opening).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            if (opening.IsCompletedSuccessfully)
            {
                var stream = opening.Result;
                stream.Buffer.LevelChanged -= OnLevel;
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            LevelChanged?.Invoke(0f);
            _cancel.Dispose();
        }

        _completion.TrySetResult(result);
    }

    private async Task SynthesizeAsync(ITextToSpeech engine, SpeechRequest request, ChannelWriter<(string, SpeechClip)> clips, CancellationToken token)
    {
        Exception? error = null;
        try
        {
            await foreach (var sentence in _sentences.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var clip = await engine.SynthesizeAsync(sentence, request, token).ConfigureAwait(false);
                clip = clip.TrimSilence();
                if (clip.Samples.Length > 0)
                {
                    await clips.WriteAsync((sentence, clip), token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex;
        }
        finally
        {
            clips.TryComplete(error);
        }
    }

    private void OnLevel(float level) => LevelChanged?.Invoke(level);

    private static float[] Resample(SpeechClip clip, int rate)
    {
        if (clip.SampleRate == rate)
        {
            return clip.Samples;
        }

        var resampler = new StreamingResampler(clip.SampleRate, rate);
        var output = new SampleBuffer((int)((long)clip.Samples.Length * rate / clip.SampleRate) + 64);
        resampler.Process(clip.Samples, output);
        resampler.Flush(output);
        return output.ToArray();
    }
}
