using Clicky.Core;
using Clicky.Core.Dictation;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Clicky.Tests.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using static Clicky.Tests.Voice.SpeechGateTests;

namespace Clicky.Tests.Dictation;

public sealed class DictationInputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clicky-tests", Guid.NewGuid().ToString("N"));
    private readonly StreamingMicrophone _mic = new();
    private readonly ScriptedSpeechToText _stt = new();
    private readonly FakeSettingsStore _settings = new(ClickySettings.Default);
    private readonly ClickyPaths _paths;
    private readonly PersonalVocabulary _vocabulary;

    public DictationInputTests()
    {
        _paths = new ClickyPaths(_root);
        _vocabulary = new PersonalVocabulary(_paths.DictionaryFile, NullLogger.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Sentences_are_transcribed_while_the_user_is_still_talking()
    {
        _stt.Replies.Enqueue("First sentence.");
        _stt.Replies.Enqueue("Second one.");
        var pieces = new List<DictationPiece>();
        using var session = Input().Start(DictationMode.HandsFree);
        session.PieceReady += pieces.Add;
        Assert.Null(await session.Opened);

        _mic.Feed(Concat(Tone(4.5, -25), Noise(1.5, -60)));
        await WaitUntil(() => pieces.Count == 1);
        Assert.Equal("First sentence.", pieces[0].Text);

        _mic.Feed(Concat(Tone(2, -25), Noise(0.3, -60)));
        var result = await session.CompleteAsync();

        Assert.Equal("First sentence. Second one.", result.Text);
        Assert.Equal(2, result.Pieces.Count);
        Assert.Null(result.Problem);
        Assert.Equal(TimeSpan.FromSeconds(8.3), result.Recorded);
    }

    [Fact]
    public async Task Every_piece_goes_to_the_history_as_text()
    {
        _stt.Replies.Enqueue("Keep this safe.");
        using var session = Input().Start(DictationMode.Hold);
        await session.Opened;
        _mic.Feed(Concat(Tone(2, -25), Noise(0.3, -60)));
        await session.CompleteAsync();

        var file = Assert.Single(Directory.GetFiles(_paths.DictationHistoryDirectory, "*.txt"));
        var text = File.ReadAllText(file);
        Assert.Contains("dictation]", text, StringComparison.Ordinal);
        Assert.Contains("Keep this safe.", text, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_root, "*.wav", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task No_history_is_written_when_it_is_turned_off()
    {
        await _settings.UpdateAsync(s => s with { Dictation = s.Dictation with { KeepHistoryDays = 0 } });
        _stt.Replies.Enqueue("Private.");
        using var session = Input().Start(DictationMode.Hold);
        await session.Opened;
        _mic.Feed(Concat(Tone(2, -25), Noise(0.3, -60)));
        var result = await session.CompleteAsync();

        Assert.Equal("Private.", result.Text);
        Assert.False(Directory.Exists(_paths.DictationHistoryDirectory));
    }

    [Fact]
    public async Task The_dictionary_biases_the_engine_and_fixes_its_spelling()
    {
        _vocabulary.Save([new DictionaryEntry("Partha", ["Parta"])]);
        _stt.Replies.Enqueue("Ask Parta. New line. Thanks — bye.");
        using var session = Input().Start(DictationMode.Hold);
        await session.Opened;
        _mic.Feed(Concat(Tone(2, -25), Noise(0.3, -60)));
        var result = await session.CompleteAsync();

        Assert.Equal("Partha.", _stt.Prompts.Single());
        Assert.Equal("Ask Partha.\nThanks, bye.", result.Text);
    }

    [Fact]
    public async Task Silence_between_sentences_never_reaches_the_engine()
    {
        using var session = Input().Start(DictationMode.HandsFree);
        await session.Opened;
        _mic.Feed(Noise(30, -62));
        var result = await session.CompleteAsync();

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(0, _stt.Calls);
    }

    [Fact]
    public async Task A_mic_that_dies_mid_dictation_keeps_what_was_said()
    {
        _stt.Replies.Enqueue("Everything so far.");
        _mic.StopError = new MicrophoneException(MicrophoneProblem.Lost, "The microphone was unplugged.");
        using var session = Input().Start(DictationMode.HandsFree);
        await session.Opened;
        _mic.Feed(Concat(Tone(3, -25), Noise(0.2, -60)));
        var result = await session.CompleteAsync();

        Assert.Equal("Everything so far.", result.Text);
        Assert.Equal("The microphone was unplugged.", result.Problem);
        Assert.Equal(MicrophoneProblem.Lost, result.MicrophoneProblem);
    }

    [Fact]
    public async Task One_failed_chunk_does_not_lose_the_others()
    {
        _stt.Replies.Enqueue("!fail");
        _stt.Replies.Enqueue("This part made it.");
        using var session = Input().Start(DictationMode.HandsFree);
        await session.Opened;
        _mic.Feed(Concat(Tone(4.5, -25), Noise(1.5, -60), Tone(2, -25), Noise(0.3, -60)));
        var result = await session.CompleteAsync();

        Assert.Equal("This part made it.", result.Text);
        Assert.Equal("Whisper broke.", result.Problem);
    }

    [Fact]
    public async Task The_length_limit_stops_listening_and_says_so_once()
    {
        await _settings.UpdateAsync(s => s with { Dictation = s.Dictation with { MaxMinutes = 1 } });
        var limits = 0;
        using var session = Input().Start(DictationMode.HandsFree);
        session.LimitReached += () => Interlocked.Increment(ref limits);
        await session.Opened;
        _mic.Feed(Noise(65, -62));
        await WaitUntil(() => Volatile.Read(ref limits) == 1);
        await session.CompleteAsync();

        Assert.Equal(1, limits);
    }

    [Fact]
    public async Task A_mic_that_cannot_start_is_reported_on_Opened()
    {
        _mic.StartError = new MicrophoneException(MicrophoneProblem.AccessDenied, "Microphone access is off.");
        using var session = Input().Start(DictationMode.HandsFree);

        var problem = await session.Opened;
        var result = await session.CompleteAsync();

        Assert.Equal(MicrophoneProblem.AccessDenied, problem?.Problem);
        Assert.Equal(MicrophoneProblem.AccessDenied, result.MicrophoneProblem);
        Assert.Equal(string.Empty, result.Text);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private DictationInput Input() => new(
        _mic,
        _ => _stt,
        _settings,
        _vocabulary,
        new DictationHistory(_paths, TimeProvider.System, NullLogger<DictationHistory>.Instance),
        new RulesOnlyPolisher(),
        NullLogger<DictationInput>.Instance);

    /// <summary>A 16 kHz float mic whose audio the test pushes in while the session runs.</summary>
    private sealed class StreamingMicrophone : IMicrophone
    {
        private SpeechAudioSink? _sink;

        public MicrophoneException? StartError { get; set; }

        public MicrophoneException? StopError { get; set; }

        public Task<IMicrophoneRecording> StartAsync(string deviceId, Func<PcmFormat, SpeechAudioSink> createSink, CancellationToken cancellationToken = default)
        {
            if (StartError is not null)
            {
                throw StartError;
            }

            var format = new PcmFormat(16_000, 1, SampleEncoding.IeeeFloat);
            _sink = createSink(format);
            return Task.FromResult<IMicrophoneRecording>(new Recording(format, this));
        }

        public void Feed(float[] audio)
        {
            var bytes = AudioConversionTests.Floats(audio);
            for (var offset = 0; offset < bytes.Length; offset += 640)
            {
                _sink!.Write(bytes.AsSpan(offset, Math.Min(640, bytes.Length - offset)));
            }
        }

        private sealed class Recording(PcmFormat format, StreamingMicrophone owner) : IMicrophoneRecording
        {
            public string DeviceName => "Fake mic";

            public PcmFormat Format => format;

            public Task<MicrophoneException?> StopAsync() => Task.FromResult(owner.StopError);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedSpeechToText : ISpeechToText
    {
        public Queue<string> Replies { get; } = new();

        public List<string?> Prompts { get; } = [];

        public int Calls { get; private set; }

        public string Description => "Fake";

        public Task<Transcript> TranscribeAsync(ReadOnlyMemory<float> samples, string language, CancellationToken cancellationToken = default) =>
            TranscribeAsync(samples, language, null, cancellationToken);

        public Task<Transcript> TranscribeAsync(ReadOnlyMemory<float> samples, string language, string? prompt, CancellationToken cancellationToken = default)
        {
            Calls++;
            Prompts.Add(prompt);
            var reply = Replies.Count > 0 ? Replies.Dequeue() : "hello";
            if (reply == "!fail")
            {
                throw new SpeechToTextException("Whisper broke.");
            }

            return Task.FromResult(new Transcript(reply, language, "Fake", TimeSpan.FromMilliseconds(5)));
        }

        public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Prepare()
        {
        }
    }
}
