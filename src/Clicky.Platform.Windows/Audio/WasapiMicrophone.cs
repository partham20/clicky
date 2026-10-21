using System.Diagnostics;
using Clicky.Core.Voice;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Clicky.Platform.Windows.Audio;

/// <param name="Id">Endpoint id, for settings.voice.inputDevice.</param>
/// <param name="Name">Friendly name, e.g. "Microphone Array (Intel® Smart Sound Technology)".</param>
/// <param name="IsDefault">The Windows default input.</param>
public sealed record InputDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// The microphone through WASAPI in shared mode. The device keeps its own mix format (usually
/// 48 kHz float); <see cref="SpeechAudioSink"/> converts it to 16 kHz mono in managed code, so there
/// is no format negotiation that can fail on odd drivers.
/// </summary>
/// <remarks>
/// <para>Every recording opens the endpoint fresh, so a change of default device (plugging in a
/// headset) applies to the next Talk. Nothing is open between recordings, so the Windows
/// microphone-in-use indicator only shows while Talk is held.</para>
/// <para>Bluetooth headsets switch to their hands-free profile while the mic is open: call quality,
/// and other audio from the headset drops in quality too. That is Windows, not something an app
/// can change.</para>
/// </remarks>
public sealed class WasapiMicrophone(ILogger<WasapiMicrophone> logger) : IMicrophone
{
    // Event-driven capture with 50 ms buffers: low CPU, and still well under the arm delay.
    private const int BufferMilliseconds = 50;

    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Active input devices, default first.</summary>
    public static IReadOnlyList<InputDevice> ListInputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out var defaultDevice) ? defaultDevice.ID : null;
        defaultDevice?.Dispose();

        var devices = new List<InputDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                devices.Add(new InputDevice(device.ID, device.FriendlyName, device.ID == defaultId));
            }
        }

        return [.. devices.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public Task<IMicrophoneRecording> StartAsync(string deviceId, Func<PcmFormat, SpeechAudioSink> createSink, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createSink);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        MMDevice device;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = Resolve(enumerator, deviceId);
        }
        catch (Exception ex) when (ex is not MicrophoneException)
        {
            throw Map(ex, "the microphone");
        }

        WasapiRecorder? capture = null;
        try
        {
            capture = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(BufferMilliseconds)
                .WithMmcssThreadPriority("Audio")
                .Build();
            var format = ToPcmFormat(capture.WaveFormat ?? MixFormat(device));
            var recording = new WasapiRecording(device, capture, format, createSink(format), logger);
            capture.StartRecording();
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogDebug("Microphone {Device} started ({Format}) in {Ms:0} ms", device.FriendlyName, format, stopwatch.Elapsed.TotalMilliseconds);
            return Task.FromResult<IMicrophoneRecording>(recording);
        }
        catch (Exception ex)
        {
            var name = SafeName(device);
            capture?.Dispose();
            device.Dispose();
            if (ex is OperationCanceledException or MicrophoneException)
            {
                throw;
            }

            throw Map(ex, name);
        }
    }

    internal static MicrophoneException Map(Exception ex, string deviceName)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            switch ((uint)inner.HResult)
            {
                case 0x80070005: // E_ACCESSDENIED
                    return new MicrophoneException(
                        MicrophoneProblem.AccessDenied,
                        "Windows is blocking the microphone. Turn on Settings › Privacy & security › Microphone › \"Let desktop apps access your microphone\".",
                        ex);
                case 0x88890004: // AUDCLNT_E_DEVICE_INVALIDATED
                    return new MicrophoneException(MicrophoneProblem.Lost, $"{deviceName} was unplugged or disabled.", ex);
                case 0x8889000A: // AUDCLNT_E_DEVICE_IN_USE
                    return new MicrophoneException(MicrophoneProblem.InUse, $"Another app is using {deviceName} exclusively.", ex);
                case 0x80070490: // E_NOTFOUND
                    return new MicrophoneException(MicrophoneProblem.NoDevice, "No microphone is connected.", ex);
                case 0x88890010: // AUDCLNT_E_SERVICE_NOT_RUNNING
                    return new MicrophoneException(MicrophoneProblem.Failed, "The Windows Audio service is not running.", ex);
            }
        }

        return new MicrophoneException(MicrophoneProblem.Failed, $"Could not record from {deviceName}: {ex.Message}", ex);
    }

    private static PcmFormat ToPcmFormat(WaveFormat format)
    {
        var subtype = format is WaveFormatExtensible extensible ? extensible.SubFormat : Guid.Empty;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || subtype == SubtypeIeeeFloat;
        var isPcm = format.Encoding == WaveFormatEncoding.Pcm || subtype == SubtypePcm;

        SampleEncoding? encoding = (isFloat, isPcm, format.BitsPerSample) switch
        {
            (true, _, 32) => SampleEncoding.IeeeFloat,
            (_, true, 16) => SampleEncoding.Pcm16,
            (_, true, 24) => SampleEncoding.Pcm24,
            (_, true, 32) => SampleEncoding.Pcm32,
            _ => null,
        };

        if (encoding is null)
        {
            throw new MicrophoneException(MicrophoneProblem.Failed, $"The microphone uses a format Clicky cannot read ({format}).");
        }

        var pcm = new PcmFormat(format.SampleRate, format.Channels, encoding.Value);
        pcm.Validate();
        return pcm;
    }

    private static WaveFormat MixFormat(MMDevice device)
    {
        using var client = device.CreateAudioClient();
        return client.MixFormat;
    }

    private static string SafeName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return "the microphone";
        }
    }

    private MMDevice Resolve(MMDeviceEnumerator enumerator, string deviceId)
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

                logger.LogWarning("Input device {Device} is {State}; using the Windows default instead", device.FriendlyName, device.State);
                device.Dispose();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
            {
                logger.LogWarning("Input device {DeviceId} is not available ({Message}); using the Windows default instead", deviceId, ex.Message);
            }
        }

        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out var fallback))
        {
            throw new MicrophoneException(MicrophoneProblem.NoDevice, "No microphone is connected or enabled.");
        }

        return fallback;
    }

    private sealed class WasapiRecording : IMicrophoneRecording
    {
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

        private readonly MMDevice _device;
        private readonly WasapiRecorder _capture;
        private readonly SpeechAudioSink _sink;
        private readonly ILogger _logger;
        private readonly TaskCompletionSource<Exception?> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stopRequested;
        private int _disposed;

        public WasapiRecording(MMDevice device, WasapiRecorder capture, PcmFormat format, SpeechAudioSink sink, ILogger logger)
        {
            _device = device;
            _capture = capture;
            _sink = sink;
            _logger = logger;
            Format = format;
            DeviceName = device.FriendlyName;
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
        }

        public string DeviceName { get; }

        public PcmFormat Format { get; }

        public async Task<MicrophoneException?> StopAsync()
        {
            RequestStop();
            Exception? error;
            try
            {
                error = await _stopped.Task.WaitAsync(StopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                return new MicrophoneException(MicrophoneProblem.Failed, $"{DeviceName} did not stop.", ex);
            }

            return error is null ? null : Map(error, DeviceName);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            RequestStop();
            try
            {
                await _stopped.Task.WaitAsync(StopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Microphone {Device} did not stop within {Timeout}", DeviceName, StopTimeout);
            }

            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _device.Dispose();
        }

        private void RequestStop()
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) != 0 || _stopped.Task.IsCompleted)
            {
                return;
            }

            try
            {
                _capture.StopRecording();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                _stopped.TrySetResult(ex);
            }
        }

        private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        {
            if ((flags & AudioClientBufferFlags.Silent) == 0)
            {
                _sink.Write(buffer);
                return;
            }

            // The engine says "treat as silence" and the bytes may be anything.
            Span<byte> silence = stackalloc byte[Math.Min(buffer.Length, 4096)];
            silence.Clear();
            for (var remaining = buffer.Length; remaining > 0; remaining -= silence.Length)
            {
                _sink.Write(silence[..Math.Min(silence.Length, remaining)]);
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e) => _stopped.TrySetResult(e.Exception);
    }
}
