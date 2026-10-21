using System.Text.RegularExpressions;

namespace Clicky.Core.Voice;

/// <param name="Text">What to synthesize.</param>
/// <param name="ContinuesSentence">The rest of the sentence the chunk before it started: a short pause, not a sentence gap.</param>
public readonly record struct SpeechChunk(string Text, bool ContinuesSentence);

/// <summary>
/// Cuts sentences into smaller pieces for engines whose time per call grows with the length of the
/// text (the local voice): the first piece of a reply is kept short so its first sound comes
/// sooner, and long sentences later on are cut at their commas so each is ready before the one
/// ahead of it has finished playing.
/// </summary>
/// <remarks>
/// Cuts only where a speaker would pause anyway: after a comma, semicolon, colon or dash, or, for
/// the first piece only, before a joining word ("and", "so", "because"). A sentence with neither
/// is left whole.
/// </remarks>
public static partial class SpeechChunker
{
    /// <summary>A first sentence longer than this many words is cut near its start.</summary>
    public const int FirstChunkWords = 6;

    /// <summary>Later sentences longer than this many words are cut at commas.</summary>
    public const int LongSentenceWords = 18;

    /// <summary>No piece shorter than this, except the first (one word, "Sure," is fine there).</summary>
    public const int MinChunkWords = 5;

    private const int FirstCutLatest = 12;

    private static readonly HashSet<string> JoiningWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "but", "so", "because", "or", "then", "which", "while", "when", "if", "since", "although", "though",
    };

    public static IReadOnlyList<SpeechChunk> Split(string sentence, bool first)
    {
        ArgumentNullException.ThrowIfNull(sentence);
        var words = Words().Matches(sentence);
        if (words.Count == 0)
        {
            return [];
        }

        var limit = first ? FirstChunkWords : LongSentenceWords;
        if (words.Count <= limit)
        {
            return [new SpeechChunk(sentence.Trim(), false)];
        }

        var chunks = new List<SpeechChunk>();
        var start = 0;
        if (first && FirstCut(sentence, words) is { } cut)
        {
            chunks.Add(new SpeechChunk(sentence[..cut].Trim(), false));
            start = cut;
        }

        // The rest: cut after clause marks while both sides keep at least MinChunkWords words.
        var rest = sentence[start..];
        var restWords = Words().Matches(rest);
        if (restWords.Count > LongSentenceWords)
        {
            var pieceStart = 0;
            var pieceWords = 0;
            for (var i = 0; i < restWords.Count - MinChunkWords; i++)
            {
                pieceWords++;
                if (pieceWords >= MinChunkWords && EndsClause(restWords[i].Value))
                {
                    var end = restWords[i].Index + restWords[i].Length;
                    chunks.Add(new SpeechChunk(rest[pieceStart..end].Trim(), chunks.Count > 0));
                    pieceStart = end;
                    pieceWords = 0;
                }
            }

            rest = rest[pieceStart..];
        }

        if (rest.Trim().Length > 0)
        {
            chunks.Add(new SpeechChunk(rest.Trim(), chunks.Count > 0));
        }

        return chunks;
    }

    // Where to end the first piece: after a clause mark within the first FirstCutLatest words, else
    // before a joining word from the fourth word on. Null to keep the sentence whole.
    private static int? FirstCut(string sentence, MatchCollection words)
    {
        var latest = Math.Min(FirstCutLatest, words.Count - MinChunkWords);
        for (var i = 0; i < latest; i++)
        {
            if (EndsClause(words[i].Value))
            {
                return words[i].Index + words[i].Length;
            }
        }

        for (var i = 3; i <= latest; i++)
        {
            if (JoiningWords.Contains(words[i].Value))
            {
                return words[i].Index;
            }
        }

        return null;
    }

    private static bool EndsClause(string word) => word[^1] is ',' or ';' or ':' or '—' or '–' || word is "-" or "—" or "–";

    [GeneratedRegex(@"\S+")]
    private static partial Regex Words();
}
