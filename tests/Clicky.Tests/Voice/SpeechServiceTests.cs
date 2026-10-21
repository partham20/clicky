using System.Net;
using System.Security.Cryptography;
using System.Text;
using Clicky.Core.Secrets;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Voice;

public sealed class SpeechServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "clicky-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Model_download_is_verified_then_moved_into_place()
    {
        var content = RandomNumberGenerator.GetBytes(300_000);
        var model = FakeModel(content);
        using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));
        using var downloader = new ModelDownloader(http, _directory, NullLogger<ModelDownloader>.Instance);
        var progress = new List<ModelDownloadProgress>();

        Assert.False(downloader.IsAvailable(model));
        var path = await downloader.EnsureAsync(model, new SyncProgress<ModelDownloadProgress>(progress.Add));

        Assert.True(downloader.IsAvailable(model));
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
        Assert.Equal(1.0, progress[^1].Fraction);
        Assert.Equal([Path.GetFileName(path)], Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_model_that_does_not_match_its_hash_is_discarded()
    {
        var content = RandomNumberGenerator.GetBytes(10_000);
        var model = FakeModel(content) with { Sha256 = new string('0', 64) };
        using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));
        using var downloader = new ModelDownloader(http, _directory, NullLogger<ModelDownloader>.Instance);

        var error = await Assert.ThrowsAsync<SpeechToTextException>(() => downloader.EnsureAsync(model));

        Assert.Contains("checksum", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task A_failed_model_download_leaves_nothing_behind()
    {
        var model = FakeModel([1, 2, 3]);
        using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var downloader = new ModelDownloader(http, _directory, NullLogger<ModelDownloader>.Instance);

        var error = await Assert.ThrowsAsync<SpeechToTextException>(() => downloader.EnsureAsync(model));

        Assert.Contains("404", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_directory) && Directory.EnumerateFileSystemEntries(_directory).Any());
    }

    [Fact]
    public void Every_known_model_has_a_sha256_and_size()
    {
        Assert.All(WhisperModels.All, m =>
        {
            Assert.Matches("^[0-9a-f]{64}$", m.Sha256);
            Assert.True(m.Size > 10_000_000);
            Assert.StartsWith("ggml-", m.FileName, StringComparison.Ordinal);
        });
        Assert.NotNull(WhisperModels.Find(WhisperModels.DefaultGpu));
        Assert.NotNull(WhisperModels.Find(" SMALL.EN-Q5_1 "));
        Assert.True(WhisperModels.Find(WhisperModels.DefaultCpu)!.EnglishOnly);
    }

    [Fact]
    public async Task OpenAi_request_carries_key_model_language_and_wav()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        using var http = new HttpClient(new FakeHandler(request =>
        {
            sent = request;
            body = Encoding.Latin1.GetString(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "text": " Turn on dark mode. " }""") };
        }));
        var secrets = new FakeSecrets { [SecretNames.OpenAiApiKey] = "sk-test" };
        var settings = new FakeSettingsStore(ClickySettings.Default with { Voice = new VoiceSettings(Engine: SpeechEngine.Cloud) });
        var stt = new OpenAiSpeechToText(http, secrets, settings, NullLogger<OpenAiSpeechToText>.Instance);

        var transcript = await stt.TranscribeAsync(new float[16_000], "en");

        Assert.Equal("Turn on dark mode.", transcript.Text);
        Assert.Equal(OpenAiSpeechToText.Endpoint, sent!.RequestUri);
        Assert.Equal("Bearer sk-test", sent.Headers.Authorization!.ToString());
        Assert.Contains("name=model", body, StringComparison.Ordinal);
        Assert.Contains(VoiceSettings.DefaultCloudModel, body, StringComparison.Ordinal);
        Assert.Contains("name=language", body, StringComparison.Ordinal);
        Assert.Contains("filename=speech.wav", body, StringComparison.Ordinal);
        Assert.Contains("RIFF", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAi_auto_language_sends_no_language()
    {
        string? body = null;
        using var http = new HttpClient(new FakeHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "text": "hi" }""") };
        }));
        var stt = new OpenAiSpeechToText(http, new FakeSecrets { [SecretNames.OpenAiApiKey] = "k" }, new FakeSettingsStore(ClickySettings.Default), NullLogger<OpenAiSpeechToText>.Instance);

        await stt.TranscribeAsync(new float[100], VoiceSettings.AutoLanguage);

        Assert.DoesNotContain("name=language", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAi_failures_become_readable_messages()
    {
        using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{ "error": { "message": "Incorrect API key provided" } }"""),
        }));
        var stt = new OpenAiSpeechToText(http, new FakeSecrets { [SecretNames.OpenAiApiKey] = "bad" }, new FakeSettingsStore(ClickySettings.Default), NullLogger<OpenAiSpeechToText>.Instance);

        var error = await Assert.ThrowsAsync<SpeechToTextException>(() => stt.TranscribeAsync(new float[100], "en"));
        Assert.Contains("rejected the API key", error.Message, StringComparison.Ordinal);

        Assert.Contains(
            "out of credit: You exceeded your quota",
            OpenAiSpeechToText.DescribeFailure(HttpStatusCode.TooManyRequests, """{ "error": { "message": "You exceeded your quota" } }"""),
            StringComparison.Ordinal);
        Assert.Contains("HTTP 500", OpenAiSpeechToText.DescribeFailure(HttpStatusCode.InternalServerError, "<html>"), StringComparison.Ordinal);
    }

    [Fact]
    public void Voice_settings_default_and_normalize()
    {
        var voice = ClickySettings.Default.Voice;
        Assert.Equal(SpeechEngine.Local, voice.Engine);
        Assert.Equal("en", voice.Language);
        Assert.Equal(WhisperModels.DefaultGpu, voice.LocalModel);
        Assert.True(voice.UseGpu);
        Assert.Equal(200, voice.MinSpeechMs);
        Assert.False(ClickySettings.Default.Debug.SaveAudio);

        var repaired = new VoiceSettings((SpeechEngine)42, " HI ", "", null!, true, " ", null!, -5, 100_000).Normalize();
        Assert.Equal(new VoiceSettings(SpeechEngine.Local, "hi", WhisperModels.DefaultGpu, WhisperModels.DefaultCpu, true, VoiceSettings.DefaultCloudModel, string.Empty, 0, VoiceSettings.MaxMaxRecordingSeconds), repaired);
        Assert.Equal(new VoiceSettings(), (ClickySettings.Default with { Voice = null! }).Normalize().Voice);
    }

    [Fact]
    public void Voice_properties_missing_from_the_file_keep_their_defaults()
    {
        var json = """{ "voice": { "engine": "Cloud" }, "debug": { "saveAudio": true } }""";

        var loaded = System.Text.Json.JsonSerializer.Deserialize(json, SettingsJsonContext.Default.ClickySettings)!.Normalize();

        Assert.Equal(SpeechEngine.Cloud, loaded.Voice.Engine);
        Assert.Equal(new VoiceSettings(SpeechEngine.Cloud), loaded.Voice);
        Assert.True(loaded.Debug.SaveAudio);
    }

    private static WhisperModel FakeModel(byte[] content) =>
        new("test", "ggml-test.bin", content.Length, Convert.ToHexStringLower(SHA256.HashData(content)), true);

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}

internal sealed class FakeSecrets : Dictionary<string, string>, ISecretStore
{
    public string? Read(string name) => TryGetValue(name, out var value) ? value : null;

    public void Write(string name, string value) => this[name] = value;

    public void Delete(string name) => Remove(name);
}

internal sealed class FakeSettingsStore(ClickySettings settings) : ISettingsStore
{
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public ClickySettings Current { get; private set; } = settings.Normalize();

    public Task UpdateAsync(Func<ClickySettings, ClickySettings> mutate, CancellationToken cancellationToken = default)
    {
        var previous = Current;
        Current = mutate(Current).Normalize();
        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, Current, SettingsChangeSource.App));
        return Task.CompletedTask;
    }
}
