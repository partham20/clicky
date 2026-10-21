namespace Clicky.Core.Voice;

/// <param name="Id">Speaker name in the model, e.g. "af_heart"; what settings.speech.localVoice holds.</param>
/// <param name="Speaker">Speaker number the model is asked for.</param>
/// <param name="Name">Display name, e.g. "Heart".</param>
/// <param name="Language">BCP-47 tag of the accent, e.g. "en-US".</param>
/// <param name="Female">For the Settings list.</param>
public sealed record LocalVoice(string Id, int Speaker, string Name, string Language, bool Female)
{
    public string Label => $"{Name} ({(Female ? "female" : "male")}, {Language})";
}

/// <param name="Name">Name used in settings.json and as the folder name, e.g. "kokoro-multi-lang-v1_0".</param>
/// <param name="Title">Display name.</param>
/// <param name="Size">Exact size of the archive in bytes.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the archive; a download that does not match is discarded.</param>
/// <param name="ExtractedSize">About how much disk the unpacked model takes, for the Settings text.</param>
/// <param name="Voices">Voices offered in Settings, best first.</param>
public sealed record LocalVoiceModel(string Name, string Title, long Size, string Sha256, long ExtractedSize, IReadOnlyList<LocalVoice> Voices)
{
    /// <summary>Where the archive is downloaded from: sherpa-onnx's model releases on GitHub.</summary>
    public Uri DownloadUri => new($"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{Name}.tar.bz2");

    public string SizeText => $"{Size / 1_000_000.0:0} MB";

    public LocalVoice? FindVoice(string? id) =>
        Voices.FirstOrDefault(v => string.Equals(v.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>The local voice models Clicky knows how to download and check.</summary>
/// <remarks>
/// <para>Kokoro-82M (Apache-2.0): the most natural open voice that runs comfortably on a laptop CPU
/// in October 2026, about 0.3 to 0.4 times real time on four threads. The full-precision export is
/// used on purpose: ONNX Runtime runs the int8 one about three times slower on x64 CPUs.</para>
/// <para>Size and hash are of the sherpa-onnx release asset as of October 2026. If upstream
/// re-uploads it the download fails its check; point settings.speech.localModelPath at an unpacked
/// copy instead.</para>
/// </remarks>
public static class LocalVoiceModels
{
    public const string DefaultModel = "kokoro-multi-lang-v1_0";

    /// <summary>Kokoro's best-graded voice.</summary>
    public const string DefaultVoice = "af_heart";

    // Speaker numbers are the model's own (its id2speaker metadata); only English is listed in
    // Settings, but any of the 54 names works in settings.json (see LanguageOf).
    public static LocalVoiceModel Kokoro { get; } = new(
        DefaultModel,
        "Kokoro v1.0",
        349_906_910,
        "c5f7e2d2caf082bc1d20fb70334a61d99d20b484500aad32e7cf84c128ea3298",
        401_000_000,
        [
            new("af_heart", 3, "Heart", "en-US", Female: true),
            new("af_bella", 2, "Bella", "en-US", Female: true),
            new("af_nicole", 6, "Nicole", "en-US", Female: true),
            new("af_aoede", 1, "Aoede", "en-US", Female: true),
            new("af_kore", 5, "Kore", "en-US", Female: true),
            new("af_sarah", 9, "Sarah", "en-US", Female: true),
            new("af_nova", 7, "Nova", "en-US", Female: true),
            new("af_sky", 10, "Sky", "en-US", Female: true),
            new("af_alloy", 0, "Alloy", "en-US", Female: true),
            new("af_jessica", 4, "Jessica", "en-US", Female: true),
            new("af_river", 8, "River", "en-US", Female: true),
            new("am_michael", 16, "Michael", "en-US", Female: false),
            new("am_fenrir", 14, "Fenrir", "en-US", Female: false),
            new("am_puck", 18, "Puck", "en-US", Female: false),
            new("am_echo", 12, "Echo", "en-US", Female: false),
            new("am_eric", 13, "Eric", "en-US", Female: false),
            new("am_liam", 15, "Liam", "en-US", Female: false),
            new("am_onyx", 17, "Onyx", "en-US", Female: false),
            new("am_adam", 11, "Adam", "en-US", Female: false),
            new("am_santa", 19, "Santa", "en-US", Female: false),
            new("bf_emma", 21, "Emma", "en-GB", Female: true),
            new("bf_isabella", 22, "Isabella", "en-GB", Female: true),
            new("bf_alice", 20, "Alice", "en-GB", Female: true),
            new("bf_lily", 23, "Lily", "en-GB", Female: true),
            new("bm_george", 26, "George", "en-GB", Female: false),
            new("bm_fable", 25, "Fable", "en-GB", Female: false),
            new("bm_lewis", 27, "Lewis", "en-GB", Female: false),
            new("bm_daniel", 24, "Daniel", "en-GB", Female: false),
        ]);

    public static IReadOnlyList<LocalVoiceModel> All { get; } = [Kokoro];

    /// <summary>Every speaker of Kokoro v1.0, by number, for names that are not in <see cref="LocalVoiceModel.Voices"/>.</summary>
    internal static string[] KokoroSpeakers { get; } =
    [
        "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore", "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky",
        "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael", "am_onyx", "am_puck", "am_santa",
        "bf_alice", "bf_emma", "bf_isabella", "bf_lily", "bm_daniel", "bm_fable", "bm_george", "bm_lewis",
        "ef_dora", "em_alex", "ff_siwis", "hf_alpha", "hf_beta", "hm_omega", "hm_psi", "if_sara", "im_nicola",
        "jf_alpha", "jf_gongitsune", "jf_nezumi", "jf_tebukuro", "jm_kumo", "pf_dora", "pm_alex", "pm_santa",
        "zf_xiaobei", "zf_xiaoni", "zf_xiaoxiao", "zf_xiaoyi", "zm_yunjian", "zm_yunxi", "zm_yunxia", "zm_yunyang", "em_santa",
    ];

    public static LocalVoiceModel? Find(string? name) =>
        All.FirstOrDefault(m => string.Equals(m.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Speaker number for a voice setting: a Kokoro name ("af_heart"), or a plain number for other
    /// models. Null when the name is unknown; empty is the default voice.
    /// </summary>
    public static int? SpeakerOf(string? voice)
    {
        voice = voice?.Trim();
        if (string.IsNullOrEmpty(voice))
        {
            return Array.IndexOf(KokoroSpeakers, DefaultVoice);
        }

        if (int.TryParse(voice, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        var index = Array.FindIndex(KokoroSpeakers, name => string.Equals(name, voice, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : index;
    }

    /// <summary>
    /// The espeak-ng language a Kokoro voice reads in, from the first letter of its name (Kokoro's own
    /// convention), and the lexicon file that goes with it, if any. espeak-ng calls British English "en".
    /// </summary>
    public static (string Language, string? Lexicon) LanguageOf(string? voice) =>
        (string.IsNullOrWhiteSpace(voice) ? DefaultVoice : voice.Trim().ToLowerInvariant())[0] switch
        {
            'b' => ("en", "lexicon-gb-en.txt"),
            'e' => ("es", null),
            'f' => ("fr", null),
            'h' => ("hi", null),
            'i' => ("it", null),
            'j' => ("ja", null),
            'p' => ("pt-br", null),
            'z' => ("cmn", "lexicon-zh.txt"),
            _ => ("en-us", "lexicon-us-en.txt"),
        };
}
