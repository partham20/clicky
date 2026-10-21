using Clicky.Core.Voice;

namespace Clicky.Core.Settings;

/// <summary>settings.speech: how replies are spoken, and when Clicky stays quiet and shows them as text instead.</summary>
/// <remarks>Positional so missing properties read as these defaults (see <see cref="CaptureSettings"/>).</remarks>
public sealed record SpeechSettings(
    bool QuietMode = false,
    VoiceOutputEngine Engine = VoiceOutputEngine.Windows,
    string WindowsVoice = "",
    string CloudModel = SpeechSettings.DefaultCloudModel,
    string CloudVoice = SpeechSettings.DefaultCloudVoice,
    string LocalVoice = LocalVoiceModels.DefaultVoice,
    string LocalModel = LocalVoiceModels.DefaultModel,
    string LocalModelPath = "",
    int LocalThreads = 0,
    double Rate = 1.0,
    double Volume = 1.0,
    string OutputDevice = "",
    bool QuietInCalls = true,
    bool QuietWhenSharing = true,
    bool QuietInFocusAssist = true,
    IReadOnlyList<string>? IgnoreMicApps = null)
{
    public const string DefaultCloudModel = "gpt-4o-mini-tts";
    public const string DefaultCloudVoice = "coral";
    public const int MaxLocalThreads = 16;
    public const double MinRate = 0.5;
    public const double MaxRate = 2.0;

    /// <summary>Never speak: every reply shows as a text card by the buddy. Also in the tray menu.</summary>
    public bool QuietMode { get; init; } = QuietMode;

    public VoiceOutputEngine Engine { get; init; } = Engine;

    /// <summary>Windows voice id (see Settings › Voice); empty uses the Windows default voice.</summary>
    public string WindowsVoice { get; init; } = WindowsVoice;

    /// <summary>OpenAI speech model for the cloud engine. The API key is the one speech-to-text uses.</summary>
    public string CloudModel { get; init; } = CloudModel;

    /// <summary>OpenAI voice name: alloy, ash, ballad, coral, echo, fable, nova, onyx, sage, shimmer, verse.</summary>
    public string CloudVoice { get; init; } = CloudVoice;

    /// <summary>Voice of the local model, e.g. "af_heart" (Kokoro) or a speaker number; empty for the model's default.</summary>
    public string LocalVoice { get; init; } = LocalVoice;

    /// <summary>Which downloadable local model to use (see <see cref="LocalVoiceModels"/>); ignored when <see cref="LocalModelPath"/> is set.</summary>
    public string LocalModel { get; init; } = LocalModel;

    /// <summary>
    /// A folder with a sherpa-onnx text-to-speech model (Kokoro or Piper/VITS) to use instead of a
    /// downloaded one, e.g. a Piper voice unpacked by hand. Empty downloads <see cref="LocalModel"/>.
    /// </summary>
    public string LocalModelPath { get; init; } = LocalModelPath;

    /// <summary>CPU threads for the local voice; 0 picks half the logical cores, at most 4.</summary>
    public int LocalThreads { get; init; } = LocalThreads;

    /// <summary>Speaking speed, 1 is normal; clamped to <see cref="MinRate"/>..<see cref="MaxRate"/>.</summary>
    public double Rate { get; init; } = Rate;

    /// <summary>0..1, on top of the Windows volume.</summary>
    public double Volume { get; init; } = Volume;

    /// <summary>Output device id; empty follows the Windows default output.</summary>
    public string OutputDevice { get; init; } = OutputDevice;

    /// <summary>Stay quiet while another app has the microphone open (Teams, Zoom, Meet, Discord, …).</summary>
    public bool QuietInCalls { get; init; } = QuietInCalls;

    /// <summary>Stay quiet while a known screen-sharing toolbar is on screen.</summary>
    public bool QuietWhenSharing { get; init; } = QuietWhenSharing;

    /// <summary>Stay quiet during Focus Assist / Do not disturb, presentation mode and full-screen apps.</summary>
    public bool QuietInFocusAssist { get; init; } = QuietInFocusAssist;

    /// <summary>
    /// Apps that keep the microphone open without being in a call (NVIDIA Broadcast, a dictation
    /// tool), by exe name or packaged app name, e.g. "NVIDIA Broadcast.exe". Matched case-insensitively
    /// against the end of the name Windows records.
    /// </summary>
    public IReadOnlyList<string> IgnoreMicApps { get; init; } = IgnoreMicApps ?? [];

    /// <summary>The voice setting that goes with <paramref name="engine"/>.</summary>
    public string VoiceFor(VoiceOutputEngine engine) => engine switch
    {
        VoiceOutputEngine.Cloud => CloudVoice,
        VoiceOutputEngine.Local => LocalVoice,
        _ => WindowsVoice,
    };

    public SpeechSettings Normalize() => this with
    {
        Engine = Enum.IsDefined(Engine) ? Engine : VoiceOutputEngine.Windows,
        WindowsVoice = WindowsVoice?.Trim() ?? string.Empty,
        CloudModel = string.IsNullOrWhiteSpace(CloudModel) ? DefaultCloudModel : CloudModel.Trim(),
        CloudVoice = string.IsNullOrWhiteSpace(CloudVoice) ? DefaultCloudVoice : CloudVoice.Trim().ToLowerInvariant(),
        LocalVoice = LocalVoice?.Trim() ?? string.Empty,
        LocalModel = string.IsNullOrWhiteSpace(LocalModel) ? LocalVoiceModels.DefaultModel : LocalModel.Trim(),
        LocalModelPath = LocalModelPath?.Trim().Trim('"') ?? string.Empty,
        LocalThreads = Math.Clamp(LocalThreads, 0, MaxLocalThreads),
        Rate = double.IsFinite(Rate) ? Math.Clamp(Rate, MinRate, MaxRate) : 1.0,
        Volume = double.IsFinite(Volume) ? Math.Clamp(Volume, 0, 1) : 1.0,
        OutputDevice = OutputDevice?.Trim() ?? string.Empty,
        IgnoreMicApps = [.. (IgnoreMicApps ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())],
    };

    // Records compare collections by reference; settings change detection needs content equality.
    public bool Equals(SpeechSettings? other) =>
        other is not null
        && QuietMode == other.QuietMode
        && Engine == other.Engine
        && WindowsVoice == other.WindowsVoice
        && CloudModel == other.CloudModel
        && CloudVoice == other.CloudVoice
        && LocalVoice == other.LocalVoice
        && LocalModel == other.LocalModel
        && LocalModelPath == other.LocalModelPath
        && LocalThreads == other.LocalThreads
        && Rate.Equals(other.Rate)
        && Volume.Equals(other.Volume)
        && OutputDevice == other.OutputDevice
        && QuietInCalls == other.QuietInCalls
        && QuietWhenSharing == other.QuietWhenSharing
        && QuietInFocusAssist == other.QuietInFocusAssist
        && (ReferenceEquals(IgnoreMicApps, other.IgnoreMicApps) || (IgnoreMicApps ?? []).SequenceEqual(other.IgnoreMicApps ?? [], StringComparer.Ordinal));

    public override int GetHashCode() => HashCode.Combine(QuietMode, Engine, WindowsVoice, LocalVoice, Rate, Volume, OutputDevice, IgnoreMicApps?.Count ?? 0);
}
