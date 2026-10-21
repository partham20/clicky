using System.Diagnostics;
using System.Runtime.InteropServices;
using Clicky.Core.Voice;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Clicky.Platform.Windows.Audio;

/// <param name="Id">Endpoint id, for settings.speech.outputDevice.</param>
/// <param name="Name">Friendly name, e.g. "Speakers (Realtek(R) Audio)".</param>
/// <param name="IsDefault">The Windows default output.</param>
public sealed record OutputDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// The speakers through WASAPI in shared mode, in the device's own mix format (usually 48 kHz
/// float stereo). The speech pipeline resamples to that rate in managed code; mono is copied to
/// every channel here.
/// </summary>
/// <remarks>
/// <para>Opened per reply and closed after it, so a change of default device (plugging in
/// headphones) applies to the next reply, and nothing runs between replies.</para>
/// <para>30 ms of buffering: barge-in is heard within about that plus the 15 ms fade. The stream
/// is tagged as speech, which Windows uses for ducking and routing policy.</para>
/// </remarks>
public sealed class WasapiSpeaker(ILogger<WasapiSpeaker> logger) : ISpeaker
{
    private const int LatencyMilliseconds = 30;

    /// <summary>Active output devices, default first.</summary>
    public static IReadOnlyList<OutputDevice> ListOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var defaultDevice) ? defaultDevice.ID : null;
        defaultDevice?.Dispose();

        var devices = new List<OutputDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                devices.Add(new OutputDevice(device.ID, device.FriendlyName, device.ID == defaultId));
            }
        }

        return [.. devices.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The device a reply would play on: the configured one if it is active, else the default. Null if there is none.</summary>
    internal static MMDevice? Resolve(MMDeviceEnumerator enumerator, string deviceId, ILogger? logger = null)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                var device = enumerator.GetDevice(deviceId);
                if (device.State == DeviceState.Active)
                {
                    return device;
                }

                logger?.LogWarning("Output device {Device} is {State}; using the Windows default instead", device.FriendlyName, device.State);
                device.Dispose();
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                logger?.LogWarning("Output device {DeviceId} is not available ({Message}); using the Windows default instead", deviceId, ex.Message);
            }
        }

        return enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var fallback) ? fallback : null;
    }

    public Task<ISpeakerSession> OpenAsync(string deviceId, float gain, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        MMDevice device;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = Resolve(enumerator, deviceId, logger) ?? throw new SpeakerException(SpeakerProblem.NoDevice, "No speakers or headphones are connected.");
        }
        catch (Exception ex) when (ex is not SpeakerException)
        {
            throw Map(ex, "the speakers");
        }

        WasapiPlayer? player = null;
        try
        {
            player = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(LatencyMilliseconds)
                .WithCategory(AudioStreamCategory.Speech)
                .WithMmcssThreadPriority("Audio")
                .Build();

            var mix = player.DeviceMixFormat;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
            var buffer = new SpeakerBuffer(format.SampleRate, gain);
            var session = new WasapiSession(device, player, buffer, format, logger);
            player.Init(session.Source);
            player.Play();
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogDebug("Speaker {Device} started ({Rate} Hz {Channels} ch) in {Ms:0} ms", device.FriendlyName, format.SampleRate, format.Channels, stopwatch.Elapsed.TotalMilliseconds);
            return Task.FromResult<ISpeakerSession>(session);
        }
        catch (Exception ex)
        {
            var name = SafeName(device);
            player?.Dispose();
            device.Dispose();
            if (ex is OperationCanceledException or SpeakerException)
            {
                throw;
            }

            throw Map(ex, name);
        }
    }

    internal static SpeakerException Map(Exception ex, string deviceName)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            switch ((uint)inner.HResult)
            {
                case 0x88890004: // AUDCLNT_E_DEVICE_INVALIDATED
                    return new SpeakerException(SpeakerProblem.Lost, $"{deviceName} was unplugged or disabled.", ex);
                case 0x8889000A: // AUDCLNT_E_DEVICE_IN_USE
                    return new SpeakerException(SpeakerProblem.InUse, $"Another app is using {deviceName} exclusively.", ex);
                case 0x80070490: // E_NOTFOUND
                    return new SpeakerException(SpeakerProblem.NoDevice, "No speakers or headphones are connected.", ex);
                case 0x88890010: // AUDCLNT_E_SERVICE_NOT_RUNNING
                    return new SpeakerException(SpeakerProblem.Failed, "The Windows Audio service is not running.", ex);
            }
        }

        return new SpeakerException(SpeakerProblem.Failed, $"Could not play on {deviceName}: {ex.Message}", ex);
    }

    private static string SafeName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return "the speakers";
        }
    }

    private sealed class WasapiSession : ISpeakerSession
    {
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

        private readonly MMDevice _device;
        private readonly WasapiPlayer _player;
        private readonly ILogger _logger;
        private readonly TaskCompletionSource<SpeakerException?> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public WasapiSession(MMDevice device, WasapiPlayer player, SpeakerBuffer buffer, WaveFormat format, ILogger logger)
        {
            _device = device;
            _player = player;
            _logger = logger;
            Buffer = buffer;
            DeviceName = device.FriendlyName;
            Source = new BufferSource(buffer, format);
            player.PlaybackStopped += OnPlaybackStopped;
        }

        public string DeviceName { get; }

        public SpeakerBuffer Buffer { get; }

        public Task<SpeakerException?> Stopped => _stopped.Task;

        internal IWaveProvider Source { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await _player.DisposeAsync().AsTask().WaitAsync(StopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Speaker {Device} did not stop within {Timeout}", DeviceName, StopTimeout);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Speaker {Device} failed while stopping", DeviceName);
            }

            _player.PlaybackStopped -= OnPlaybackStopped;
            _device.Dispose();
            _stopped.TrySetResult(null);
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception is { } error)
            {
                var problem = Map(error, DeviceName);
                _logger.LogWarning(error, "Speaker {Device} stopped: {Problem}", DeviceName, problem.Message);
                Buffer.Abort();
                _stopped.TrySetResult(problem);
            }
            else
            {
                _stopped.TrySetResult(null);
            }
        }
    }

    /// <summary>Pulls mono samples from the buffer on the render thread and copies them to every channel.</summary>
    private sealed class BufferSource(SpeakerBuffer buffer, WaveFormat format) : IWaveProvider
    {
        private float[] _mono = new float[4800];

        public WaveFormat WaveFormat => format;

        public int Read(Span<byte> bytes)
        {
            var output = MemoryMarshal.Cast<byte, float>(bytes);
            var channels = format.Channels;
            var frames = output.Length / channels;
            if (_mono.Length < frames)
            {
                _mono = new float[frames];
            }

            var mono = _mono.AsSpan(0, frames);
            buffer.Render(mono);
            for (var frame = 0; frame < frames; frame++)
            {
                var sample = mono[frame];
                var offset = frame * channels;
                for (var channel = 0; channel < channels; channel++)
                {
                    output[offset + channel] = sample;
                }
            }

            // Keep the stream running (silence once drained) until the pipeline closes it.
            return frames * channels * sizeof(float);
        }
    }
}
