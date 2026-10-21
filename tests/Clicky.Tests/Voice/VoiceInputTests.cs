using Clicky.Core;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Voice;

public sealed class VoiceInputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clicky-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeMicrophone _mic = new();
    private readonly FakeSpeechToText _stt = new();
    private readonly FakeSettingsStore _settings = new(ClickySettings.Default);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Speech_is_trimmed_transcribed_and_cleaned()
    {
        _mic.Audio = SpeechGateTests.Concat(SpeechGateTests.Noise(1, -60), SpeechGateTests.Tone(1, -25), SpeechGateTests.Noise(0.5, -60));
        _stt.Reply = "  Where is the export button? [BLANK_AUDIO]";

        using var session = Input().Start();
        var result = await session.CompleteAsync();

        Assert.Equal(VoiceOutcome.Transcribed, result.Outcome);
        Assert.Equal("Where is the export button?", result.Text);
        Assert.Equal(TimeSpan.FromSeconds(2.5), result.Recorded);
        Assert.Equal("en", _stt.Language);
        Assert.Equal(1, _stt.Prepared);

        // 1 s of speech plus 250 ms either side.
        Assert.Equal(24_000, _stt.Samples);
        Assert.True(_mic.Last!.Stopped);
        Assert.True(_mic.Last.Disposed);
    }

    [Fact]
    public async Task Silence_never_reaches_the_speech_engine()
    {
        _mic.Audio = SpeechGateTests.Noise(2, -65);

        using var session = Input().Start();
        var result = await session.CompleteAsync();

        Assert.Equal(VoiceOutcome.NoSpeech, result.Outcome);
        Assert.Equal(0, _stt.Calls);
    }

    [Fact]
    public async Task Whisper_inventions_on_a_short_noise_count_as_no_speech()
    {
        _mic.Audio = SpeechGateTests.Concat(SpeechGateTests.Noise(1, -60), SpeechGateTests.Tone(0.4, -20), SpeechGateTests.Noise(1, -60));
        _stt.Reply = "Thank you.";

        using var session = Input().Start();
        var result = await session.CompleteAsync();

        Assert.Equal(VoiceOutcome.NoSpeech, result.Outcome);
        Assert.Equal(1, _stt.Calls);
        Assert.Equal("Thank you.", result.Transcript!.Text);
    }

    [Fact]
    public async Task A_blocked_microphone_fails_with_its_reason()
    {
        _mic.StartError = new MicrophoneException(MicrophoneProblem.AccessDenied, "Microphone access is off.");

        using var session = Input().Start();
        var result = await session.CompleteAsync();

        Assert.Equal(VoiceOutcome.Failed, result.Outcome);
        Assert.Equal(MicrophoneProblem.AccessDenied, result.MicrophoneProblem);
        Assert.Equal("Microphone access is off.", result.Problem);
    }

    [Fact]
    public async Task A_device_lost_mid_sentence_still_transcribes_what_was_heard()
    {
        _mic.Audio = SpeechGateTests.Tone(1, -25);
        _mic.StopError = new MicrophoneException(MicrophoneProblem.Lost, "The microphone was unplugged.");
        _stt.Reply = "Open settings";

        using var session = Input().Start();
        var result = await session.CompleteAsync();

        Assert.Equal(VoiceOutcome.Transcribed, result.Outcome);
        Assert.Equal("Open settings", result.Text);
    }

    [Fact]
    public async Task Engine_errors_fail_with_the_engine_message()
    {
        _mic.Audio = SpeechGateTests.Tone(1, -25);
        _stt.Error = new SpeechToTextException("The speech model is still downloading (40%).");

        using var session = Input().Start();
        var result = await session.CompleteAsync();

        Assert.Equal(VoiceOutcome.Failed, result.Outcome);
        Assert.Equal("The speech model is still downloading (40%).", result.Problem);
        Assert.Null(result.MicrophoneProblem);
    }

    [Fact]
    public async Task Cancel_releases_the_microphone_and_transcribes_nothing()
    {
        _mic.Audio = SpeechGateTests.Tone(1, -25);

        var session = Input().Start();
        await _mic.Started.Task;
        session.Cancel();
        await _mic.Last!.DisposedTask.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await session.CompleteAsync();
        Assert.Equal(VoiceOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, _stt.Calls);
        session.Dispose();
    }

    [Fact]
    public async Task Level_events_flow_while_recording()
    {
        _mic.Audio = SpeechGateTests.Tone(1, -25);
        var levels = new List<float>();

        using var session = Input().Start();
        session.LevelChanged += levels.Add;
        await session.CompleteAsync();

        Assert.NotEmpty(levels);
        Assert.All(levels, l => Assert.InRange(l, 0.6f, 0.9f));
    }

    [Fact]
    public async Task Debug_audio_is_saved_only_when_asked()
    {
        _mic.Audio = SpeechGateTests.Tone(1, -25);
        var paths = new ClickyPaths(_root);

        using (var session = Input(paths).Start())
        {
            await session.CompleteAsync();
        }

        Assert.False(Directory.Exists(paths.DebugAudioDirectory));

        await _settings.UpdateAsync(s => s with { Debug = s.Debug with { SaveAudio = true } });
        using (var session = Input(paths).Start())
        {
            await session.CompleteAsync();
        }

        var wav = Assert.Single(Directory.GetFiles(paths.DebugAudioDirectory, "*.wav"));
        Assert.Equal(44 + (16_000 * 2), new FileInfo(wav).Length);
    }

    private VoiceInput Input(ClickyPaths? paths = null) =>
        new(_mic, _ => _stt, _settings, paths ?? new ClickyPaths(_root), NullLogger<VoiceInput>.Instance);

    private sealed class FakeMicrophone : IMicrophone
    {
        public float[] Audio { get; set; } = [];

        public MicrophoneException? StartError { get; set; }

        public MicrophoneException? StopError { get; set; }

        public FakeRecording? Last { get; private set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IMicrophoneRecording> StartAsync(string deviceId, Func<PcmFormat, SpeechAudioSink> createSink, CancellationToken cancellationToken = default)
        {
            if (StartError is not null)
            {
                throw StartError;
            }

            // A 48 kHz mono float device, as WASAPI would report it.
            var format = new PcmFormat(48_000, 1, SampleEncoding.IeeeFloat);
            var sink = createSink(format);
            Last = new FakeRecording(format, sink, Upsample(Audio), StopError);
            Started.TrySetResult();
            return Task.FromResult<IMicrophoneRecording>(Last);
        }

        private static float[] Upsample(float[] audio)
        {
            var resampler = new StreamingResampler(16_000, 48_000);
            var output = new SampleBuffer();
            resampler.Process(audio, output);
            resampler.Flush(output);
            return output.ToArray();
        }
    }

    private sealed class FakeRecording(PcmFormat format, SpeechAudioSink sink, float[] audio, MicrophoneException? stopError) : IMicrophoneRecording
    {
        public string DeviceName => "Fake mic";

        public PcmFormat Format => format;

        public bool Stopped { get; private set; }

        public bool Disposed { get; private set; }

        public TaskCompletionSource DisposedTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MicrophoneException?> StopAsync()
        {
            // Deliver the whole recording in 10 ms device buffers, as the capture thread would have.
            var bytes = AudioConversionTests.Floats(audio);
            for (var offset = 0; offset < bytes.Length; offset += 1920)
            {
                sink.Write(bytes.AsSpan(offset, Math.Min(1920, bytes.Length - offset)));
            }

            Stopped = true;
            return Task.FromResult(stopError);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            DisposedTask.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSpeechToText : ISpeechToText
    {
        public string Reply { get; set; } = "hello";

        public SpeechToTextException? Error { get; set; }

        public int Calls { get; private set; }

        public int Samples { get; private set; }

        public string? Language { get; private set; }

        public string Description => "Fake";

        public Task<Transcript> TranscribeAsync(ReadOnlyMemory<float> samples, string language, CancellationToken cancellationToken = default)
        {
            Calls++;
            Samples = samples.Length;
            Language = language;
            if (Error is not null)
            {
                throw Error;
            }

            return Task.FromResult(new Transcript(Reply, language, "Fake", TimeSpan.FromMilliseconds(5)));
        }

        public int Prepared { get; private set; }

        public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Prepare() => Prepared++;
    }
}
