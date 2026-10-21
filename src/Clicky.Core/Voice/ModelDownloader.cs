using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Voice;

/// <param name="Model">What is downloading.</param>
/// <param name="Received">Bytes so far.</param>
public readonly record struct ModelDownloadProgress(WhisperModel Model, long Received)
{
    public double Fraction => Model.Size <= 0 ? 0 : Math.Clamp((double)Received / Model.Size, 0, 1);
}

/// <summary>
/// Downloads Whisper models into a folder and checks them. A file is only ever visible under its
/// final name once its size and SHA-256 match, so a crash or a bad network never leaves a
/// half-written model that later fails to load.
/// </summary>
public sealed class ModelDownloader(HttpClient http, string directory, ILogger<ModelDownloader> logger) : IDisposable
{
    private const int BufferSize = 1 << 20;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Directory => directory;

    public void Dispose() => _gate.Dispose();

    public string PathOf(WhisperModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Path.Combine(directory, model.FileName);
    }

    /// <summary>
    /// True if the model file is present with the right size. The hash is checked when it is
    /// downloaded, not on every start: hashing 600 MB costs about a second.
    /// </summary>
    public bool IsAvailable(WhisperModel model)
    {
        var file = new FileInfo(PathOf(model));
        return file.Exists && file.Length == model.Size;
    }

    /// <summary>Returns the model's path, downloading it first if needed. One download at a time.</summary>
    /// <exception cref="SpeechToTextException">The download failed or the file did not match.</exception>
    public async Task<string> EnsureAsync(WhisperModel model, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var path = PathOf(model);
        if (IsAvailable(model))
        {
            return path;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsAvailable(model))
            {
                return path;
            }

            await DownloadAsync(model, path, progress, cancellationToken).ConfigureAwait(false);
            return path;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadAsync(WhisperModel model, string path, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(directory);
        var partial = path + ".partial";
        logger.LogInformation("Downloading speech model {Model} ({Size}) from {Uri}", model.Name, model.SizeText, model.DownloadUri);
        var started = TimeProvider.System.GetTimestamp();

        try
        {
            using var response = await http.GetAsync(model.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SpeechToTextException($"Downloading the speech model {model.Name} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                await using (file.ConfigureAwait(false))
                {
                    var buffer = new byte[BufferSize];
                    var lastReport = 0L;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        received += read;
                        if (received > model.Size)
                        {
                            throw new SpeechToTextException($"The speech model {model.Name} is larger than expected; the download was discarded.");
                        }

                        if (received - lastReport >= model.Size / 200 || received == model.Size)
                        {
                            lastReport = received;
                            progress?.Report(new ModelDownloadProgress(model, received));
                        }
                    }
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (received != model.Size || !string.Equals(actual, model.Sha256, StringComparison.Ordinal))
            {
                throw new SpeechToTextException(
                    $"The speech model {model.Name} did not match its checksum ({received:N0} bytes, SHA-256 {actual}); the download was discarded.");
            }

            File.Move(partial, path, overwrite: true);
            logger.LogInformation(
                "Speech model {Model} downloaded and verified in {Seconds:0} s",
                model.Name,
                TimeProvider.System.GetElapsedTime(started).TotalSeconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDelete(partial);
            throw new SpeechToTextException($"Downloading the speech model {model.Name} failed: {ex.Message}", ex);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}
