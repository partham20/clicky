using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Clicky.Core.Voice;

/// <summary>
/// Tidies raw speech-to-text output and drops the text Whisper invents from silence and noise.
/// </summary>
/// <remarks>
/// Whisper was trained on subtitled video, so non-speech comes back as subtitle furniture
/// ("[BLANK_AUDIO]", "(music)", "♪") or as credits ("Thanks for watching!"). Those are always
/// removed. A few short fillers ("Thank you.", "you", "Bye.") are also things people really say,
/// so they are only dropped when the recording held very little speech.
/// </remarks>
public static partial class TranscriptCleaner
{
    /// <summary>Below this much detected speech, a filler-only transcript is treated as invented.</summary>
    public static readonly TimeSpan FillerSpeechThreshold = TimeSpan.FromMilliseconds(600);

    private static readonly HashSet<string> Credits = new(StringComparer.Ordinal)
    {
        "thanks for watching",
        "thank you for watching",
        "thanks for watching and see you next time",
        "please subscribe",
        "like and subscribe",
        "subtitles by the amaraorg community",
        "subtitles by",
        "transcription by castingwords",
        "the end",
    };

    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "you",
        "thank you",
        "thanks",
        "thank you so much",
        "bye",
        "okay",
        "oh",
        "uh",
        "um",
        "hmm",
        "so",
    };

    /// <summary>
    /// Returns the cleaned transcript, or an empty string when nothing real was said.
    /// </summary>
    /// <param name="raw">Engine output, possibly several segments joined.</param>
    /// <param name="speech">How much speech <see cref="SpeechDetector"/> found in the audio.</param>
    public static string Clean(string? raw, TimeSpan speech)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = Annotation().Replace(raw, " ");
        text = text.Replace('♪', ' ').Replace('♫', ' ');
        text = Whitespace().Replace(text, " ").Trim();

        var key = Key(text);
        if (key.Length == 0 || Credits.Contains(key))
        {
            return string.Empty;
        }

        if (speech < FillerSpeechThreshold && Fillers.Contains(key))
        {
            return string.Empty;
        }

        return text;
    }

    /// <summary>Lowercase letters, digits and single spaces only: "Thank you!" and "thank you." compare equal.</summary>
    internal static string Key(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLower(c, CultureInfo.InvariantCulture));
                pendingSpace = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    // [BLANK_AUDIO], (music), (silence), *coughs*: anything bracketed is a description, not speech.
    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", RegexOptions.CultureInvariant)]
    private static partial Regex Annotation();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
