using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Clicky.Core.Secrets;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Voice;

/// <summary>
/// Cloud text-to-speech through OpenAI's speech endpoint. The Claude API has no text-to-speech, so
/// like cloud speech-to-text this is a second provider, and it uses the same stored key.
/// </summary>
/// <remarks>Asks for raw PCM (24 kHz, 16-bit, mono) so there is nothing to decode.</remarks>
public sealed class OpenAiTextToSpeech(HttpClient http, ISecretStore secrets, ISettingsStore settings, ILogger<OpenAiTextToSpeech> logger) : ITextToSpeech
{
    public static readonly Uri Endpoint = new("https://api.openai.com/v1/audio/speech");

    /// <summary>A sentence that takes longer than this would leave a long gap; give up and say so.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public const int SampleRate = 24_000;

    public string Description => $"OpenAI {settings.Current.Speech.CloudModel} ({settings.Current.Speech.CloudVoice})";

    public async Task<SpeechClip> SynthesizeAsync(string text, SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var key = OpenAiKey.Read(secrets) ?? throw new TextToSpeechException(
            "Cloud voice needs an OpenAI API key. Add one in Settings › Voice, or switch the voice back to Windows.");
        var speech = settings.Current.Speech;

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(
                new SpeechBody(speech.CloudModel, text, string.IsNullOrWhiteSpace(request.Voice) ? speech.CloudVoice : request.Voice, "pcm", Math.Clamp(request.Rate, 0.25, 4.0)),
                OpenAiJsonContext.Default.SpeechBody),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(message, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = System.Text.Encoding.UTF8.GetString(body);
                logger.LogWarning("OpenAI speech failed: HTTP {Status} {Body}", (int)response.StatusCode, error.Length <= 500 ? error : error[..500] + "…");
                throw new TextToSpeechException(DescribeFailure(response.StatusCode, error));
            }

            return new SpeechClip(DecodePcm16(body), SampleRate, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TextToSpeechException($"OpenAI did not answer within {RequestTimeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException ex)
        {
            throw new TextToSpeechException($"Could not reach OpenAI: {ex.Message}", ex);
        }
    }

    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (OpenAiKey.Read(secrets) is null)
        {
            logger.LogWarning("Cloud voice is selected but no OpenAI API key is stored");
        }

        return Task.CompletedTask;
    }

    internal static float[] DecodePcm16(ReadOnlySpan<byte> pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]) / 32768f;
        }

        return samples;
    }

    internal static string DescribeFailure(HttpStatusCode status, string body) => status switch
    {
        HttpStatusCode.Unauthorized => "OpenAI rejected the API key. Check it in Settings › Voice.",
        HttpStatusCode.TooManyRequests => "OpenAI is rate limiting or the account is out of credit.",
        _ => $"OpenAI speech failed (HTTP {(int)status}): {OpenAiSpeechToText.TryReadErrorMessage(body) ?? "no details"}",
    };
}

internal sealed record SpeechBody(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("voice")] string Voice,
    [property: JsonPropertyName("response_format")] string ResponseFormat,
    [property: JsonPropertyName("speed")] double Speed);

[JsonSerializable(typeof(SpeechBody))]
internal sealed partial class OpenAiJsonContext : JsonSerializerContext;

/// <summary>The OpenAI key shared by cloud speech-to-text and cloud voice.</summary>
internal static class OpenAiKey
{
    /// <summary>Credential Manager first, then the OPENAI_API_KEY environment variable (development).</summary>
    public static string? Read(ISecretStore secrets)
    {
        var key = secrets.Read(SecretNames.OpenAiApiKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        }

        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }
}
