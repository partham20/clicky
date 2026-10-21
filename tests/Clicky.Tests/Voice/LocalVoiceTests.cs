using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using ICSharpCode.SharpZipLib.BZip2;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Voice;

public sealed class LocalVoiceTests : IDisposable
{
    /// <summary>
    /// A sherpa-onnx Kokoro or Piper folder for the real-synthesis test. CI sets it after fetching
    /// the model; elsewhere that test only checks its inputs.
    /// </summary>
    private const string ModelVariable = "CLICKY_TEST_VOICE_MODEL";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "clicky-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Settings_default_to_kokoro_heart_and_normalize()
    {
        var speech = new SpeechSettings();
        Assert.Equal(VoiceOutputEngine.Windows, speech.Engine);
        Assert.Equal("af_heart", speech.LocalVoice);
        Assert.Equal(LocalVoiceModels.DefaultModel, speech.LocalModel);

        var odd = (speech with { LocalModel = " ", LocalModelPath = " \"C:\\voices\\kokoro\" ", LocalThreads = 99, LocalVoice = " bf_emma " }).Normalize();
        Assert.Equal(LocalVoiceModels.DefaultModel, odd.LocalModel);
        Assert.Equal("C:\\voices\\kokoro", odd.LocalModelPath);
        Assert.Equal(SpeechSettings.MaxLocalThreads, odd.LocalThreads);
        Assert.Equal("bf_emma", odd.LocalVoice);
        Assert.Equal(0, (speech with { LocalThreads = -3 }).Normalize().LocalThreads);

        Assert.Equal("bf_emma", odd.VoiceFor(VoiceOutputEngine.Local));
        Assert.Equal(speech.CloudVoice, odd.VoiceFor(VoiceOutputEngine.Cloud));
        Assert.NotEqual(speech, speech with { LocalVoice = "am_puck" });
    }

    [Fact]
    public async Task The_local_engine_round_trips_through_settings_json()
    {
        var file = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(file, """{ "speech": { "engine": "Local", "localVoice": "bm_george" } }""");

        using var store = JsonSettingsStore.Open(file, NullLogger<JsonSettingsStore>.Instance);

        Assert.Equal(VoiceOutputEngine.Local, store.Current.Speech.Engine);
        Assert.Equal("bm_george", store.Current.Speech.LocalVoice);
        Assert.Equal(LocalVoiceModels.DefaultModel, store.Current.Speech.LocalModel);
    }

    [Fact]
    public void Catalog_voices_match_the_model_speaker_numbers()
    {
        Assert.Equal(54, LocalVoiceModels.KokoroSpeakers.Length);
        Assert.All(LocalVoiceModels.All, m =>
        {
            Assert.Matches("^[0-9a-f]{64}$", m.Sha256);
            Assert.True(m.Size > 100_000_000);
            Assert.StartsWith("https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/", m.DownloadUri.ToString(), StringComparison.Ordinal);
            Assert.All(m.Voices, v => Assert.Equal(v.Speaker, LocalVoiceModels.SpeakerOf(v.Id)));
            Assert.Equal(m.Voices.Count, m.Voices.Select(v => v.Id).Distinct().Count());
        });
        Assert.NotNull(LocalVoiceModels.Kokoro.FindVoice(LocalVoiceModels.DefaultVoice));
        Assert.Same(LocalVoiceModels.Kokoro, LocalVoiceModels.Find(" KOKORO-multi-lang-v1_0 "));
    }

    [Theory]
    [InlineData("af_heart", 3)]
    [InlineData("AM_PUCK", 18)]
    [InlineData("em_santa", 53)]
    [InlineData("", 3)]
    [InlineData("7", 7)]
    [InlineData("nobody", null)]
    [InlineData("-1", null)]
    public void Speakers_are_found_by_name_or_number(string voice, int? speaker) =>
        Assert.Equal(speaker, LocalVoiceModels.SpeakerOf(voice));

    [Theory]
    [InlineData("af_heart", "en-us", "lexicon-us-en.txt")]
    [InlineData("bm_george", "en", "lexicon-gb-en.txt")]
    [InlineData("hf_alpha", "hi", null)]
    [InlineData("", "en-us", "lexicon-us-en.txt")]
    [InlineData("3", "en-us", "lexicon-us-en.txt")]
    public void Voices_read_in_their_own_language(string voice, string language, string? lexicon) =>
        Assert.Equal((language, lexicon), LocalVoiceModels.LanguageOf(voice));

    [Theory]
    [InlineData(0, 16, 4)]
    [InlineData(0, 6, 3)]
    [InlineData(0, 1, 1)]
    [InlineData(6, 4, 6)]
    public void Threads_default_to_half_the_cores_at_most_four(int setting, int processors, int expected) =>
        Assert.Equal(expected, LocalTextToSpeech.ThreadsFor(setting, processors));

    [Fact]
    public void Model_folders_are_recognized_by_their_files()
    {
        var kokoro = Folder("kokoro", "model.onnx", "voices.bin", "tokens.txt", "espeak-ng-data/phontab", "lexicon-us-en.txt", "lexicon-gb-en.txt", "dict/x");
        var piper = Folder("piper", "en_US-amy-medium.onnx", "en_US-amy-medium.onnx.json", "tokens.txt", "espeak-ng-data/phontab");
        var neither = Folder("neither", "model.onnx", "tokens.txt");

        Assert.Equal(LocalModelKind.Kokoro, LocalTextToSpeech.DetectKind(kokoro));
        Assert.Equal(LocalModelKind.Vits, LocalTextToSpeech.DetectKind(piper));
        Assert.Null(LocalTextToSpeech.DetectKind(neither));
        Assert.Null(LocalTextToSpeech.DetectKind(Path.Combine(_directory, "missing")));

        var us = LocalTextToSpeech.ConfigFor(kokoro, LocalModelKind.Kokoro, "af_heart", 3);
        Assert.Equal(Path.Combine(kokoro, "lexicon-us-en.txt"), us.Model.Kokoro.Lexicon);
        Assert.Equal("en-us", us.Model.Kokoro.Lang);
        Assert.Equal(Path.Combine(kokoro, "dict"), us.Model.Kokoro.DictDir);
        Assert.Equal(3, us.Model.NumThreads);
        Assert.Equal(1, us.MaxNumSentences);
        Assert.Equal(Path.Combine(kokoro, "lexicon-gb-en.txt"), LocalTextToSpeech.ConfigFor(kokoro, LocalModelKind.Kokoro, "bf_emma", 1).Model.Kokoro.Lexicon);
        Assert.True(string.IsNullOrEmpty(LocalTextToSpeech.ConfigFor(kokoro, LocalModelKind.Kokoro, "hf_alpha", 1).Model.Kokoro.Lexicon));
        Assert.Equal(Path.Combine(piper, "en_US-amy-medium.onnx"), LocalTextToSpeech.ConfigFor(piper, LocalModelKind.Vits, "0", 1).Model.Vits.Model);
    }

    [Fact]
    public async Task A_model_download_is_verified_unpacked_and_moved_into_place()
    {
        var archive = Archive(("kokoro-test/", null), ("kokoro-test/model.onnx", "m"), ("kokoro-test/voices.bin", "v"), ("kokoro-test/tokens.txt", "t"), ("kokoro-test/espeak-ng-data/phontab", "p"));
        var model = FakeModel(archive);
        using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }));
        using var downloader = new LocalVoiceDownloader(http, _directory, NullLogger<LocalVoiceDownloader>.Instance);
        var progress = new List<LocalVoiceDownloadProgress>();

        Assert.False(downloader.IsAvailable(model));
        var path = await downloader.EnsureAsync(model, new SyncProgress<LocalVoiceDownloadProgress>(progress.Add));

        Assert.True(downloader.IsAvailable(model));
        Assert.Equal(Path.Combine(_directory, model.Name), path);
        Assert.Equal("t", await File.ReadAllTextAsync(Path.Combine(path, "tokens.txt")));
        Assert.Equal("p", await File.ReadAllTextAsync(Path.Combine(path, "espeak-ng-data", "phontab")));
        Assert.Equal(LocalModelKind.Kokoro, LocalTextToSpeech.DetectKind(path));
        Assert.True(progress[0] is { Received: 0, Unpacking: false });
        Assert.True(progress[^1] is { Unpacking: true });
        Assert.Equal([model.Name], Directory.EnumerateFileSystemEntries(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_model_that_does_not_match_its_hash_is_discarded()
    {
        var archive = Archive(("k/model.onnx", "m"));
        var model = FakeModel(archive) with { Sha256 = new string('0', 64) };
        using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }));
        using var downloader = new LocalVoiceDownloader(http, _directory, NullLogger<LocalVoiceDownloader>.Instance);

        var error = await Assert.ThrowsAsync<TextToSpeechException>(() => downloader.EnsureAsync(model));

        Assert.Contains("checksum", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public async Task An_archive_without_a_model_or_escaping_its_folder_is_discarded()
    {
        var empty = Archive(("k/README.md", "hi"));
        var escaping = Archive(("k/../../evil.txt", "x"), ("k/model.onnx", "m"));
        foreach (var (archive, message) in new[] { (empty, "has no model.onnx"), (escaping, "outside") })
        {
            var model = FakeModel(archive);
            using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }));
            using var downloader = new LocalVoiceDownloader(http, _directory, NullLogger<LocalVoiceDownloader>.Instance);

            var error = await Assert.ThrowsAsync<TextToSpeechException>(() => downloader.EnsureAsync(model));

            Assert.Contains(message, error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
        }

        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_directory)!, "evil.txt")));
    }

    [Fact]
    public async Task Replies_before_the_model_is_downloaded_say_it_is_on_its_way()
    {
        var release = new TaskCompletionSource();
        var archive = Archive(("k/model.onnx", "m"), ("k/voices.bin", "v"), ("k/tokens.txt", "t"), ("k/espeak-ng-data/phontab", "p"));
        using var http = new HttpClient(new FakeHandler(_ =>
        {
            release.Task.Wait(TimeSpan.FromSeconds(10));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        }));
        using var downloader = new LocalVoiceDownloader(http, _directory, NullLogger<LocalVoiceDownloader>.Instance);
        var settings = new FakeSettingsStore(ClickySettings.Default with { Speech = new SpeechSettings(Engine: VoiceOutputEngine.Local) });
        using var tts = new LocalTextToSpeech(downloader, settings, NullLogger<LocalTextToSpeech>.Instance);
        var events = new List<LocalVoiceDownloadProgress?>();
        tts.DownloadProgressChanged += (_, p) => { lock (events) { events.Add(p); } };

        // The engine downloads the catalog model; this handler serves a small archive instead, held
        // back until the first replies have been turned away.
        var error = await Assert.ThrowsAsync<TextToSpeechException>(() => tts.SynthesizeAsync("Hi.", new SpeechRequest(string.Empty, 1)));
        Assert.Contains("still downloading", error.Message, StringComparison.Ordinal);
        Assert.Contains("still downloading", (await Assert.ThrowsAsync<TextToSpeechException>(() => tts.SynthesizeAsync("Hi.", default))).Message, StringComparison.Ordinal);

        // The catalog model's hash does not match the fake archive, so the download fails; the next reply says so once.
        release.SetResult();
        await WaitUntil(() => { lock (events) { return events.Count > 0 && events[^1] is null; } });
        var failed = await Assert.ThrowsAsync<TextToSpeechException>(() => tts.SynthesizeAsync("Hi.", default));
        Assert.Contains("could not be downloaded", failed.Message, StringComparison.Ordinal);
        Assert.Contains("checksum", failed.Message, StringComparison.Ordinal);
        Assert.False(downloader.IsAvailable(LocalVoiceModels.Kokoro));
    }

    [Fact]
    public async Task A_configured_folder_that_is_not_a_model_fails_clearly()
    {
        var folder = Folder("bad", "readme.txt");
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("no downloads")));
        using var downloader = new LocalVoiceDownloader(http, _directory, NullLogger<LocalVoiceDownloader>.Instance);
        var settings = new FakeSettingsStore(ClickySettings.Default with { Speech = new SpeechSettings(Engine: VoiceOutputEngine.Local, LocalModelPath: folder) });
        using var tts = new LocalTextToSpeech(downloader, settings, NullLogger<LocalTextToSpeech>.Instance);

        var error = await Assert.ThrowsAsync<TextToSpeechException>(() => tts.SynthesizeAsync("Hi.", default));

        Assert.Contains("is not a sherpa-onnx voice model", error.Message, StringComparison.Ordinal);
        Assert.Contains("bad", tts.Description, StringComparison.Ordinal);
    }

    /// <summary>Runs only where <see cref="ModelVariable"/> points at a model (Windows CI fetches Kokoro).</summary>
    [Fact]
    public async Task A_real_model_speaks_each_sentence_quickly()
    {
        var folder = Environment.GetEnvironmentVariable(ModelVariable);
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("no downloads")));
        using var downloader = new LocalVoiceDownloader(http, _directory, NullLogger<LocalVoiceDownloader>.Instance);
        var settings = new FakeSettingsStore(ClickySettings.Default with { Speech = new SpeechSettings(Engine: VoiceOutputEngine.Local, LocalModelPath: folder) });
        using var tts = new LocalTextToSpeech(downloader, settings, NullLogger<LocalTextToSpeech>.Instance);

        await tts.WarmUpAsync();
        var clip = await tts.SynthesizeAsync("Sure, the Save button is in the top right corner.", new SpeechRequest(string.Empty, 1.0));
        var british = await tts.SynthesizeAsync("Click File, then Export.", new SpeechRequest("bf_emma", 1.2));
        // Whatever the voice makes of it (espeak-ng may name the emoji), it must not throw.
        await tts.SynthesizeAsync("🙂", new SpeechRequest(string.Empty, 1.0));

        Assert.True(clip.Duration > TimeSpan.FromSeconds(1.5) && clip.Duration < TimeSpan.FromSeconds(5), $"{clip.Duration}");
        Assert.True(clip.Samples.Max(Math.Abs) > 0.1f);
        Assert.True(british.Duration > TimeSpan.FromSeconds(0.5));

        // Shared CI runners are slow and noisy: only catch a voice that could never keep up.
        Assert.True(clip.Elapsed < clip.Duration * 2, $"{clip.Elapsed.TotalMilliseconds:0} ms for {clip.Duration.TotalSeconds:0.00} s");
        Console.WriteLine($"Local voice: {clip.Elapsed.TotalMilliseconds:0} ms for {clip.Duration.TotalSeconds:0.00} s of speech");

        using var cancel = new CancellationTokenSource();
        var cancelled = tts.SynthesizeAsync("This one is cancelled while the voice is still working on it, which must not break the next.", new SpeechRequest(string.Empty, 1.0), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.True((await tts.SynthesizeAsync("Still here.", new SpeechRequest(string.Empty, 1.0))).Samples.Length > 0);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(5);
        }
    }

    private static LocalVoiceModel FakeModel(byte[] archive) =>
        new("kokoro-test", "Test voice", archive.Length, Convert.ToHexStringLower(SHA256.HashData(archive)), 1_000, []);

    private static byte[] Archive(params (string Name, string? Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var bzip = new BZip2OutputStream(output) { IsStreamOwner = false })
        using (var tar = new TarWriter(bzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                if (content is null)
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name));
                    continue;
                }

                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
            }
        }

        return output.ToArray();
    }

    private string Folder(string name, params string[] files)
    {
        var folder = Path.Combine(_directory, name);
        foreach (var file in files)
        {
            var path = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }

        return folder;
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
