using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Clicky.Core.Secrets;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Voice;

/// <summary>
/// Cloud speech-to-text through OpenAI's transcription endpoint. The Claude API has no
/// speech-to-text, so the cloud option is a second provider.
/// </summary>
/// <remarks>
/// The key comes from the secret store (<see cref="SecretNames.OpenAiApiKey"/>), falling back to
/// the OPENAI_API_KEY environment variable for development. Audio is sent as a 16-bit WAV: about
/// 32 KB per second of speech.
/// </remarks>
public sealed class OpenAiSpeechToText(HttpClient http, ISecretStore secrets, ISettingsStore settings, ILogger<OpenAiSpeechToText> logger) : ISpeechToText
{
    public static readonly Uri Endpoint = new("https://api.openai.com/v1/audio/transcriptions");

    /// <summary>A Talk transcript that takes longer than this is no use; give up and say so.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public string Description => $"OpenAI {settings.Current.Voice.CloudModel}";

    public async Task<Transcript> TranscribeAsync(ReadOnlyMemory<float> samples, string language, CancellationToken cancellationToken = default)
    {
        var key = ApiKey() ?? throw new SpeechToTextException(
            "Cloud speech-to-text needs an OpenAI API key. Add one in Settings, or switch voice.engine back to Local.");
        var model = settings.Current.Voice.CloudModel;

        using var content = new MultipartFormDataContent();
        var audio = new ByteArrayContent(WavEncoder.EncodePcm16(samples.Span));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "file", "speech.wav");
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(language) && !string.Equals(language, VoiceSettings.AutoLanguage, StringComparison.OrdinalIgnoreCase))
        {
            content.Add(new StringContent(language), "language");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("OpenAI transcription failed: HTTP {Status} {Body}", (int)response.StatusCode, Truncate(body));
                throw new SpeechToTextException(DescribeFailure(response.StatusCode, body));
            }

            using var json = JsonDocument.Parse(body);
            var text = json.RootElement.TryGetProperty("text", out var value) ? value.GetString() ?? string.Empty : string.Empty;
            return new Transcript(text.Trim(), language, Description, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpeechToTextException($"OpenAI did not answer within {RequestTimeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException ex)
        {
            throw new SpeechToTextException($"Could not reach OpenAI: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new SpeechToTextException("OpenAI sent a reply that is not valid JSON.", ex);
        }
    }

    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (ApiKey() is null)
        {
            logger.LogWarning("Cloud speech-to-text is selected but no OpenAI API key is stored");
        }

        return Task.CompletedTask;
    }

    internal static string DescribeFailure(HttpStatusCode status, string body)
    {
        var message = TryReadErrorMessage(body);
        return status switch
        {
            HttpStatusCode.Unauthorized => "OpenAI rejected the API key. Check it in Settings.",
            HttpStatusCode.TooManyRequests => $"OpenAI is rate limiting or the account is out of credit: {message ?? "HTTP 429"}",
            _ => $"OpenAI transcription failed (HTTP {(int)status}): {message ?? "no details"}",
        };
    }

    private static string? TryReadErrorMessage(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";

    private string? ApiKey()
    {
        var key = secrets.Read(SecretNames.OpenAiApiKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        }

        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }
}
