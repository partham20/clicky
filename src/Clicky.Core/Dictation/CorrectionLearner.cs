using System.Globalization;

namespace Clicky.Core.Dictation;

/// <summary>A word the user fixed by hand: speech-to-text wrote <paramref name="Heard"/>, they meant <paramref name="Written"/>.</summary>
public sealed record Correction(string Heard, string Written);

/// <summary>
/// Finds the word-level correction a user made to text Clicky just inserted, by comparing the
/// field right after insertion with the field now. Only small fixes inside the inserted text
/// count (one to three words, spelled much like the original); rewrites, deletions and new
/// typing elsewhere are not corrections.
/// </summary>
public static class CorrectionLearner
{
    public const int MaxWords = 3;

    /// <summary>Spelling distance, as a share of the longer word, beyond which a change is a rewrite.</summary>
    public const double MaxDistance = 0.5;

    /// <summary>Looser limit when both start with the same letter ("clawed" to "Claude").</summary>
    public const double MaxDistanceSameStart = 0.67;

    /// <param name="before">The whole field right after Clicky inserted its text.</param>
    /// <param name="insertStart">Where the inserted text starts in <paramref name="before"/>.</param>
    /// <param name="insertLength">Its length.</param>
    /// <param name="after">The whole field now.</param>
    public static Correction? Find(string before, int insertStart, int insertLength, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (insertStart < 0 || insertLength <= 0 || insertStart + insertLength > before.Length || string.Equals(before, after, StringComparison.Ordinal))
        {
            return null;
        }

        var prefix = 0;
        var limit = Math.Min(before.Length, after.Length);
        while (prefix < limit && before[prefix] == after[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < limit - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        // Widen the change to whole words. The widened parts are shared by both texts.
        var start = prefix;
        while (start > 0 && IsWordChar(before[start - 1]))
        {
            start--;
        }

        var oldEnd = before.Length - suffix;
        while (oldEnd < before.Length && IsWordChar(before[oldEnd]))
        {
            oldEnd++;
        }

        var newEnd = after.Length - (before.Length - oldEnd);
        if (start < insertStart || oldEnd > insertStart + insertLength || newEnd < start)
        {
            return null;
        }

        var heard = TrimPunctuation(before[start..oldEnd]);
        var written = TrimPunctuation(after[start..newEnd]);
        if (heard.Length == 0 || written.Length == 0 || string.Equals(heard, written, StringComparison.Ordinal))
        {
            return null;
        }

        var heardWords = CountWords(heard);
        var writtenWords = CountWords(written);
        if (heardWords > MaxWords || writtenWords > MaxWords || writtenWords > heardWords + 1)
        {
            return null;
        }

        // More words typed after (or before) the inserted ones is new text, and fewer is a deletion: not fixes.
        if (written.StartsWith(heard + " ", StringComparison.OrdinalIgnoreCase) || written.EndsWith(" " + heard, StringComparison.OrdinalIgnoreCase)
            || heard.StartsWith(written + " ", StringComparison.OrdinalIgnoreCase) || heard.EndsWith(" " + written, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var a = Letters(heard);
        var b = Letters(written);
        if (a.Length == 0 || b.Length == 0)
        {
            return null;
        }

        if (a == b)
        {
            // Only case or spacing changed ("clicky" to "Clicky", "git hub" to "GitHub"): worth
            // learning when the new form has capitals no sentence would give it.
            return written.Skip(1).Any(char.IsUpper) || (char.IsUpper(written[0]) && !char.IsUpper(heard[0])) ? new Correction(heard, written) : null;
        }

        var distance = (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);
        var limitShare = a[0] == b[0] ? MaxDistanceSameStart : MaxDistance;
        return distance <= limitShare ? new Correction(heard, written) : null;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’' or '-' or '_';

    private static int CountWords(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string TrimPunctuation(string text) =>
        text.Trim().Trim('.', ',', '!', '?', ';', ':', '"', '“', '”', '(', ')', '[', ']').Trim();

    private static string Letters(string text) =>
        new([.. text.Where(char.IsLetterOrDigit).Select(c => char.ToLower(c, CultureInfo.InvariantCulture))]);

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
