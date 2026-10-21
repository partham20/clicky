using System.Formats.Tar;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.BZip2;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Voice;

/// <param name="Model">What is downloading.</param>
/// <param name="Received">Archive bytes so far.</param>
/// <param name="Unpacking">The archive is verified and being unpacked (about half a minute).</param>
public readonly record struct LocalVoiceDownloadProgress(LocalVoiceModel Model, long Received, bool Unpacking)
{
    public double Fraction => Model.Size <= 0 ? 0 : Math.Clamp((double)Received / Model.Size, 0, 1);
}

/// <summary>
/// Downloads local voice models (sherpa-onnx .tar.bz2 archives) into a folder, checks them and
/// unpacks them. A model folder only ever appears under its final name once the archive matched
/// its size and SHA-256 and unpacked completely, so a crash or a bad network never leaves a
/// half-written model that later fails to load.
/// </summary>
public sealed class LocalVoiceDownloader(HttpClient http, string directory, ILogger<LocalVoiceDownloader> logger) : IDisposable
{
    private const int BufferSize = 1 << 20;

    // Files every Kokoro folder has; checked before a folder counts as a model.
    private static readonly string[] RequiredFiles = ["model.onnx", "voices.bin", "tokens.txt"];

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Directory => directory;

    public void Dispose() => _gate.Dispose();

    public string PathOf(LocalVoiceModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Path.Combine(directory, model.Name);
    }

    public bool IsAvailable(LocalVoiceModel model)
    {
        var path = PathOf(model);
        return RequiredFiles.All(f => File.Exists(Path.Combine(path, f)));
    }

    /// <summary>Returns the model's folder, downloading and unpacking it first if needed. One download at a time.</summary>
    /// <exception cref="TextToSpeechException">The download failed, did not match, or could not be unpacked.</exception>
    public async Task<string> EnsureAsync(LocalVoiceModel model, IProgress<LocalVoiceDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (IsAvailable(model))
        {
            return PathOf(model);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsAvailable(model))
            {
                await DownloadAsync(model, progress, cancellationToken).ConfigureAwait(false);
            }

            return PathOf(model);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Unpacks a .tar.bz2 into <paramref name="target"/>, refusing entries that would land outside
    /// it, and links. The archive's single top-level folder (sherpa-onnx archives have one) is dropped.
    /// </summary>
    internal static async Task ExtractAsync(Stream archive, string target, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        System.IO.Directory.CreateDirectory(root);
        using var bzip = new BZip2InputStream(archive) { IsStreamOwner = false };
        using var tar = new TarReader(bzip);
        while (await tar.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            var name = entry.Name.Replace('\\', '/').TrimStart('/');
            while (name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name[2..];
            }

            var slash = name.IndexOf('/', StringComparison.Ordinal);
            var relative = slash < 0 ? string.Empty : name[(slash + 1)..];
            if (relative.Length == 0)
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The archive entry {entry.Name} points outside the model folder.");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    System.IO.Directory.CreateDirectory(path);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await entry.ExtractToFileAsync(path, overwrite: true, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    // Links, devices and the like: a model needs none of them.
                    break;
            }
        }
    }

    private async Task DownloadAsync(LocalVoiceModel model, IProgress<LocalVoiceDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(directory);
        var final = PathOf(model);
        var archive = final + ".tar.bz2.partial";
        var unpacking = final + ".unpacking";
        logger.LogInformation("Downloading voice model {Model} ({Size}) from {Uri}", model.Name, model.SizeText, model.DownloadUri);
        var started = TimeProvider.System.GetTimestamp();

        try
        {
            progress?.Report(new LocalVoiceDownloadProgress(model, 0, Unpacking: false));
            await DownloadArchiveAsync(model, archive, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report(new LocalVoiceDownloadProgress(model, model.Size, Unpacking: true));
            TryDeleteDirectory(unpacking);
            var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                await ExtractAsync(stream, unpacking, cancellationToken).ConfigureAwait(false);
            }

            var missing = RequiredFiles.FirstOrDefault(f => !File.Exists(Path.Combine(unpacking, f)));
            if (missing is not null)
            {
                throw new TextToSpeechException($"The voice model {model.Name} has no {missing}; the download was discarded.");
            }

            TryDeleteDirectory(final);
            System.IO.Directory.Move(unpacking, final);
            logger.LogInformation(
                "Voice model {Model} downloaded, verified and unpacked in {Seconds:0} s",
                model.Name,
                TimeProvider.System.GetElapsedTime(started).TotalSeconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or FormatException
            or ICSharpCode.SharpZipLib.SharpZipBaseException)
        {
            TryDeleteDirectory(unpacking);
            throw new TextToSpeechException($"Downloading the voice model {model.Name} failed: {ex.Message}", ex);
        }
        catch
        {
            TryDeleteDirectory(unpacking);
            throw;
        }
        finally
        {
            TryDelete(archive);
        }
    }

    private async Task DownloadArchiveAsync(LocalVoiceModel model, string path, IProgress<LocalVoiceDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(model.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new TextToSpeechException($"Downloading the voice model {model.Name} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
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
                        throw new TextToSpeechException($"The voice model {model.Name} is larger than expected; the download was discarded.");
                    }

                    if (received - lastReport >= model.Size / 200 || received == model.Size)
                    {
                        lastReport = received;
                        progress?.Report(new LocalVoiceDownloadProgress(model, received, Unpacking: false));
                    }
                }
            }
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (received != model.Size || !string.Equals(actual, model.Sha256, StringComparison.Ordinal))
        {
            throw new TextToSpeechException(
                $"The voice model {model.Name} did not match its checksum ({received:N0} bytes, SHA-256 {actual}); the download was discarded.");
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

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
            {
                System.IO.Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}
