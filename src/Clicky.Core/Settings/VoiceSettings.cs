using Clicky.Core.Voice;

namespace Clicky.Core.Settings;

/// <summary>settings.voice: microphone and speech-to-text.</summary>
/// <remarks>Positional so missing properties read as these defaults (see <see cref="CaptureSettings"/>).</remarks>
public sealed record VoiceSettings(
    SpeechEngine Engine = SpeechEngine.Local,
    string Language = VoiceSettings.DefaultLanguage,
    string LocalModel = WhisperModels.DefaultGpu,
    string CpuModel = WhisperModels.DefaultCpu,
    bool UseGpu = true,
    string CloudModel = VoiceSettings.DefaultCloudModel,
    string InputDevice = "",
    int MinSpeechMs = VoiceSettings.DefaultMinSpeechMs,
    int MaxRecordingSeconds = VoiceSettings.DefaultMaxRecordingSeconds)
{
    public const string DefaultLanguage = "en";
    public const string AutoLanguage = "auto";
    public const string DefaultCloudModel = "gpt-4o-mini-transcribe";
    public const int DefaultMinSpeechMs = 200;
    public const int MaxMinSpeechMs = 3000;
    public const int DefaultMaxRecordingSeconds = 120;
    public const int MinMaxRecordingSeconds = 5;
    public const int MaxMaxRecordingSeconds = 600;

    public SpeechEngine Engine { get; init; } = Engine;

    /// <summary>ISO 639-1 code ("en", "hi", "ta") or "auto" to detect it on every request (slower).</summary>
    public string Language { get; init; } = Language;

    /// <summary>Whisper model used when the GPU runtime loads (see <see cref="WhisperModels"/>).</summary>
    public string LocalModel { get; init; } = LocalModel;

    /// <summary>Whisper model used when there is no usable GPU, or <see cref="UseGpu"/> is off.</summary>
    public string CpuModel { get; init; } = CpuModel;

    /// <summary>Run Whisper on the GPU through Vulkan when possible. Takes effect on restart.</summary>
    public bool UseGpu { get; init; } = UseGpu;

    /// <summary>OpenAI transcription model for the cloud engine. The API key lives in Windows Credential Manager.</summary>
    public string CloudModel { get; init; } = CloudModel;

    /// <summary>Input device id; empty follows the Windows default input.</summary>
    public string InputDevice { get; init; } = InputDevice;

    /// <summary>Less detected speech than this and nothing is transcribed (a tap, a cough, a silent hold).</summary>
    public int MinSpeechMs { get; init; } = MinSpeechMs;

    /// <summary>A Talk hold longer than this stops recording; what was said so far is still transcribed.</summary>
    public int MaxRecordingSeconds { get; init; } = MaxRecordingSeconds;

    public VoiceSettings Normalize() => this with
    {
        Engine = Enum.IsDefined(Engine) ? Engine : SpeechEngine.Local,
        Language = string.IsNullOrWhiteSpace(Language) ? DefaultLanguage : Language.Trim().ToLowerInvariant(),
        LocalModel = string.IsNullOrWhiteSpace(LocalModel) ? WhisperModels.DefaultGpu : LocalModel.Trim(),
        CpuModel = string.IsNullOrWhiteSpace(CpuModel) ? WhisperModels.DefaultCpu : CpuModel.Trim(),
        CloudModel = string.IsNullOrWhiteSpace(CloudModel) ? DefaultCloudModel : CloudModel.Trim(),
        InputDevice = InputDevice?.Trim() ?? string.Empty,
        MinSpeechMs = Math.Clamp(MinSpeechMs, 0, MaxMinSpeechMs),
        MaxRecordingSeconds = Math.Clamp(MaxRecordingSeconds, MinMaxRecordingSeconds, MaxMaxRecordingSeconds),
    };
}
