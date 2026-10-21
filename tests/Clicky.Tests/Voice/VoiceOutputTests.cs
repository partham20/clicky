using System.Collections.Concurrent;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Voice;

public sealed class VoiceOutputTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeSpeaker _speaker = new();
    private readonly FakeTextToSpeech _tts = new();
    private readonly FakeQuietDetector _quiet = new();
    private readonly FakeSettingsStore _settings = new(ClickySettings.Default);

    [Fact]
    public async Task Streamed_text_is_spoken_sentence_by_sentence_in_order()
    {
        using var output = Output();
        var started = new ConcurrentQueue<string>();

        var reply = output.Begin();
        reply.SentenceStarted += started.Enqueue;
        foreach (var token in new[] { "Open **Sett", "ings**. Then click", " [Voice](https://x.y/z). ", "Done" })
        {
            reply.Append(token);
        }

        reply.Complete();
        var result = await reply.Completion.WaitAsync(Timeout);

        Assert.Equal(ReplyOutcome.Spoken, result.Outcome);
        Assert.Equal(["Open Settings.", "Then click Voice.", "Done"], _tts.Texts);
        Assert.Equal(_tts.Texts, started);
        Assert.Equal(3, result.Sentences);
        Assert.Equal("Open **Settings**. Then click [Voice](https://x.y/z). Done", result.Text);
        Assert.True(_speaker.Last!.Disposed);
        Assert.Equal(1f, _speaker.Last.Gain);
    }

    [Fact]
    public async Task The_first_sentence_is_synthesized_before_the_reply_is_complete()
    {
        using var output = Output();
        var reply = output.Begin();

        reply.Append("First sentence here. And the sec");
        await WaitUntil(() => _tts.Texts.Count == 1);

        Assert.Equal(["First sentence here."], _tts.Texts);
        reply.Append("ond one.");
        reply.Complete();
        Assert.Equal(ReplyOutcome.Spoken, (await reply.Completion.WaitAsync(Timeout)).Outcome);
    }

    [Fact]
    public async Task Clips_are_resampled_to_the_device_rate_with_gaps_between_sentences()
    {
        using var output = Output();
        _tts.ClipSamples = 1600;

        var result = await output.Speak("One. Two.").Completion.WaitAsync(Timeout);

        Assert.Equal(ReplyOutcome.Spoken, result.Outcome);

        // Two 0.1 s clips at 16 kHz become 2 × 4800 samples at 48 kHz, plus one 180 ms gap.
        var gap = (int)Math.Round(VoiceOutput.SentenceGap.TotalSeconds * FakeSpeaker.Rate);
        Assert.InRange(_speaker.Last!.Buffer.Written, (2 * 4800) + gap - 4, (2 * 4800) + gap + 4);
    }

    [Fact]
    public async Task Barge_in_stops_at_once_and_drops_the_rest()
    {
        using var output = Output();
        _tts.ClipSamples = 16_000 * 30;
        var started = new TaskCompletionSource();

        var reply = output.Begin();
        reply.SentenceStarted += _ => started.TrySetResult();
        reply.Append("One. Two. Three. Four. Five. Six.");
        reply.Complete();
        await started.Task.WaitAsync(Timeout);

        Assert.True(output.IsSpeaking);
        Assert.True(output.Interrupt());
        var result = await reply.Completion.WaitAsync(Timeout);

        Assert.Equal(ReplyOutcome.Interrupted, result.Outcome);
        Assert.True(_tts.Texts.Count <= 4, $"synthesized {_tts.Texts.Count}");
        Assert.True(_speaker.Last!.Disposed);
        Assert.False(output.IsSpeaking);
        Assert.False(output.Interrupt());
    }

    [Fact]
    public async Task A_new_reply_interrupts_the_one_playing()
    {
        using var output = Output();
        _tts.ClipSamples = 16_000 * 30;

        var first = output.Speak("A long first reply.");
        await WaitUntil(() => _tts.Texts.Count == 1);
        _tts.ClipSamples = 160;
        var second = output.Speak("Second.");

        Assert.Equal(ReplyOutcome.Interrupted, (await first.Completion.WaitAsync(Timeout)).Outcome);
        Assert.Equal(ReplyOutcome.Spoken, (await second.Completion.WaitAsync(Timeout)).Outcome);
    }

    [Fact]
    public async Task In_a_call_the_reply_is_shown_as_text_and_the_speaker_never_opens()
    {
        using var output = Output();
        _quiet.Check = new QuietCheck(QuietReasons.InCall, "Microsoft Teams");

        var reply = output.Begin();
        reply.Append("Here is the answer. ");
        reply.Append("Two sentences.");
        reply.Complete();
        var result = await reply.Completion.WaitAsync(Timeout);

        Assert.False(reply.IsSpeaking);
        Assert.Equal(ReplyOutcome.ShownAsText, result.Outcome);
        Assert.Equal(QuietReasons.InCall, result.Quiet);
        Assert.Equal("You seem to be in a call (Microsoft Teams).", result.Problem);
        Assert.Equal("Here is the answer. Two sentences.", result.Text);
        Assert.Null(_speaker.Last);
        Assert.Empty(_tts.Texts);
    }

    [Fact]
    public async Task A_check_turned_off_in_settings_does_not_keep_it_quiet()
    {
        await _settings.UpdateAsync(s => s with { Speech = s.Speech with { QuietInCalls = false } });
        using var output = Output();
        _quiet.Check = new QuietCheck(QuietReasons.InCall, "Zoom");

        var result = await output.Speak("Hello.").Completion.WaitAsync(Timeout);

        Assert.Equal(ReplyOutcome.Spoken, result.Outcome);
    }

    [Fact]
    public async Task Muted_speakers_always_mean_text()
    {
        await _settings.UpdateAsync(s => s with { Speech = s.Speech with { QuietInCalls = false, QuietWhenSharing = false, QuietInFocusAssist = false } });
        using var output = Output();
        _quiet.Check = new QuietCheck(QuietReasons.Muted);

        var result = await output.Speak("Hello.").Completion.WaitAsync(Timeout);

        Assert.Equal(ReplyOutcome.ShownAsText, result.Outcome);
        Assert.Equal("Your sound is muted.", result.Problem);
    }

    [Fact]
    public async Task A_voice_failure_fails_the_reply_with_its_reason_and_releases_the_speaker()
    {
        using var output = Output();
        _tts.FailOn = 2;

        var result = await output.Speak("One. Two. Three.").Completion.WaitAsync(Timeout);

        Assert.Equal(ReplyOutcome.Failed, result.Outcome);
        Assert.Equal("voice broke", result.Problem);
        Assert.Equal("One. Two. Three.", result.Text);
        Assert.True(_speaker.Last!.Disposed);
    }

    [Fact]
    public async Task No_output_device_fails_with_a_plain_reason()
    {
        using var output = Output();
        _speaker.OpenError = new SpeakerException(SpeakerProblem.NoDevice, "none");

        var result = await output.Speak("Hello.").Completion.WaitAsync(Timeout);

        Assert.Equal(ReplyOutcome.Failed, result.Outcome);
        Assert.Equal("No speakers or headphones are connected.", result.Problem);
    }

    [Fact]
    public async Task A_device_unplugged_mid_reply_fails_without_hanging()
    {
        using var output = Output();
        _tts.ClipSamples = 16_000 * 30;
        var started = new TaskCompletionSource();

        var reply = output.Speak("A long sentence.");
        reply.SentenceStarted += _ => started.TrySetResult();
        await WaitUntil(() => _speaker.Last is { Buffer.Played: > 0 });
        _speaker.Last!.Unplug();
        var result = await reply.Completion.WaitAsync(Timeout);

        Assert.Equal(ReplyOutcome.Failed, result.Outcome);
        Assert.Equal("unplugged", result.Problem);
    }

    [Fact]
    public async Task Volume_and_voice_come_from_settings()
    {
        await _settings.UpdateAsync(s => s with { Speech = s.Speech with { Volume = 0.4, WindowsVoice = "heera", Rate = 1.5 } });
        using var output = Output();

        await output.Speak("Hi.").Completion.WaitAsync(Timeout);

        Assert.Equal(0.4f, _speaker.Last!.Gain, 3);
        Assert.Equal(new SpeechRequest("heera", 1.5), _tts.LastRequest);
    }

    [Fact]
    public async Task The_local_engine_is_asked_for_the_local_voice()
    {
        await _settings.UpdateAsync(s => s with { Speech = s.Speech with { Engine = VoiceOutputEngine.Local, WindowsVoice = "heera", LocalVoice = "bf_emma" } });
        var asked = new List<VoiceOutputEngine>();
        using var output = new VoiceOutput(_speaker, e => { asked.Add(e); return _tts; }, _quiet, _settings, NullLogger<VoiceOutput>.Instance);

        await output.Speak("Hi.").Completion.WaitAsync(Timeout);

        Assert.Equal([VoiceOutputEngine.Local], asked);
        Assert.Equal(new SpeechRequest("bf_emma", 1.0), _tts.LastRequest);
    }

    [Fact]
    public async Task A_reply_with_nothing_speakable_still_completes()
    {
        using var output = Output();

        var result = await output.Speak("```\ncode only\n```").Completion.WaitAsync(Timeout);

        Assert.Equal(ReplyOutcome.Spoken, result.Outcome);
        Assert.Equal(0, result.Sentences);
        Assert.Empty(_tts.Texts);
    }

    [Fact]
    public async Task A_prepared_speaker_is_used_by_the_next_reply()
    {
        using var output = Output();

        output.Prepare();
        output.Prepare();
        await WaitUntil(() => _speaker.Opened == 1);
        var prepared = _speaker.Last;
        var result = await output.Speak("Hi.").Completion.WaitAsync(Timeout);

        Assert.Equal(ReplyOutcome.Spoken, result.Outcome);
        Assert.Equal(1, _speaker.Opened);
        Assert.Same(prepared, _speaker.Last);
    }

    [Fact]
    public async Task A_prepared_speaker_nobody_uses_is_closed()
    {
        using var output = Output();
        output.WarmLifetime = TimeSpan.FromMilliseconds(30);

        output.Prepare();
        await WaitUntil(() => _speaker.Last is { Disposed: true });

        await output.Speak("Hi.").Completion.WaitAsync(Timeout);
        Assert.Equal(2, _speaker.Opened);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(5);
        }
    }

    private VoiceOutput Output() => new(_speaker, _ => _tts, _quiet, _settings, NullLogger<VoiceOutput>.Instance);

    private sealed class FakeQuietDetector : IQuietDetector
    {
        public QuietCheck Check { get; set; } = QuietCheck.None;

        QuietCheck IQuietDetector.Check(SpeechSettings settings) => Check;
    }

    private sealed class FakeTextToSpeech : ITextToSpeech
    {
        private readonly ConcurrentQueue<string> _texts = new();

        public int ClipSamples { get; set; } = 800;

        public int FailOn { get; set; }

        public SpeechRequest LastRequest { get; private set; }

        public IReadOnlyList<string> Texts => [.. _texts];

        public string Description => "fake";

        public Task<SpeechClip> SynthesizeAsync(string text, SpeechRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _texts.Enqueue(text);
            LastRequest = request;
            if (_texts.Count == FailOn)
            {
                throw new TextToSpeechException("voice broke");
            }

            // A square wave: loud from the first sample to the last, so trimming keeps all of it.
            var samples = new float[ClipSamples];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = i % 2 == 0 ? 0.3f : -0.3f;
            }

            return Task.FromResult(new SpeechClip(samples, 16_000, TimeSpan.FromMilliseconds(5)));
        }

        public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSpeaker : ISpeaker
    {
        public const int Rate = 48_000;

        public SpeakerException? OpenError { get; set; }

        public FakeSession? Last { get; private set; }

        public int Opened { get; private set; }

        public Task<ISpeakerSession> OpenAsync(string deviceId, float gain, CancellationToken cancellationToken = default)
        {
            if (OpenError is not null)
            {
                throw OpenError;
            }

            var session = new FakeSession(gain);
            Last = session;
            Opened++;
            return Task.FromResult<ISpeakerSession>(session);
        }
    }

    /// <summary>Renders 10 ms blocks, many per timer tick: far faster than real time.</summary>
    private sealed class FakeSession : ISpeakerSession
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource<SpeakerException?> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _pump;

        public FakeSession(float gain)
        {
            Gain = gain;
            Buffer = new SpeakerBuffer(FakeSpeaker.Rate, gain);
            _pump = Task.Run(async () =>
            {
                var block = new float[FakeSpeaker.Rate / 100];
                var playing = true;
                while (playing && !_stop.IsCancellationRequested)
                {
                    for (var i = 0; i < 20 && playing; i++)
                    {
                        playing = Buffer.Render(block);
                    }

                    await Task.Delay(1).ConfigureAwait(false);
                }

                _stopped.TrySetResult(null);
            });
        }

        public float Gain { get; }

        public bool Disposed { get; private set; }

        public string DeviceName => "Fake speakers";

        public SpeakerBuffer Buffer { get; }

        public Task<SpeakerException?> Stopped => _stopped.Task;

        public void Unplug()
        {
            _stopped.TrySetResult(new SpeakerException(SpeakerProblem.Lost, "unplugged"));
            _stop.Cancel();
            Buffer.Abort();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _pump;
            Disposed = true;
            _stop.Dispose();
        }
    }
}
