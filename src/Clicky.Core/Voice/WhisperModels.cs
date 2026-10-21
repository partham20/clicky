namespace Clicky.Core.Voice;

/// <param name="Name">Name used in settings.json, e.g. "large-v3-turbo-q5_0".</param>
/// <param name="FileName">ggml file name in the whisper.cpp model repository.</param>
/// <param name="Size">Exact file size in bytes.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the file; a download that does not match is discarded.</param>
/// <param name="EnglishOnly">".en" models: faster and better at English, ignore the language setting.</param>
public sealed record WhisperModel(string Name, string FileName, long Size, string Sha256, bool EnglishOnly)
{
    /// <summary>Where the file is downloaded from.</summary>
    public Uri DownloadUri => new($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName}");

    public string SizeText => $"{Size / 1_000_000.0:0} MB";
}

/// <summary>The Whisper models Clicky knows how to download and check.</summary>
/// <remarks>Sizes and hashes are from the Hugging Face repository's LFS metadata (October 2026).</remarks>
public static class WhisperModels
{
    /// <summary>Default when a GPU is available: near large-v3 accuracy at a fraction of the cost.</summary>
    public const string DefaultGpu = "large-v3-turbo-q5_0";

    /// <summary>Default on CPU only: fast enough for push-to-talk on a laptop CPU.</summary>
    public const string DefaultCpu = "small.en-q5_1";

    public static IReadOnlyList<WhisperModel> All { get; } =
    [
        new("large-v3-turbo-q5_0", "ggml-large-v3-turbo-q5_0.bin", 574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", false),
        new("large-v3-turbo-q8_0", "ggml-large-v3-turbo-q8_0.bin", 874_188_075, "317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1", false),
        new("large-v3-turbo", "ggml-large-v3-turbo.bin", 1_624_555_275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69", false),
        new("medium.en-q5_0", "ggml-medium.en-q5_0.bin", 539_225_533, "76733e26ad8fe1c7a5bf7531a9d41917b2adc0f20f2e4f5531688a8c6cd88eb0", true),
        new("small.en-q5_1", "ggml-small.en-q5_1.bin", 190_098_681, "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30", true),
        new("small.en", "ggml-small.en.bin", 487_614_201, "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d", true),
        new("small-q5_1", "ggml-small-q5_1.bin", 190_085_487, "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", false),
        new("base.en", "ggml-base.en.bin", 147_964_211, "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002", true),
        new("base.en-q5_1", "ggml-base.en-q5_1.bin", 59_721_011, "4baf70dd0d7c4247ba2b81fafd9c01005ac77c2f9ef064e00dcf195d0e2fdd2f", true),
        new("tiny.en-q5_1", "ggml-tiny.en-q5_1.bin", 32_166_155, "c77c5766f1cef09b6b7d47f21b546cbddd4157886b3b5d6d4f709e91e66c7c2b", true),
    ];

    public static WhisperModel? Find(string? name) =>
        All.FirstOrDefault(m => string.Equals(m.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
}
