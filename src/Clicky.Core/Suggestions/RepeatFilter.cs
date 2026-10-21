using System.Globalization;
using System.Text.RegularExpressions;

namespace Clicky.Core.Suggestions;

/// <summary>
/// Spots an idea that was already shown, so the user never sees the same thing twice in a row of
/// mornings even when the editor words it differently. Rules, not a model call, so it can be
/// trusted and tested: the same item (message ID, URL) in the sources, the same topic, or titles
/// and topics that share most of their meaningful words.
/// </summary>
public static partial class RepeatFilter
{
    /// <summary>Share of meaningful words two ideas must have in common to count as the same idea.</summary>
    public const double SameIdeaOverlap = 0.5;

    /// <summary>Share of the shorter idea's words found in the other: a short rewording of a longer idea.</summary>
    public const double SmallerOverlap = 0.6;

    private static readonly HashSet<string> s_stopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "to", "for", "of", "on", "in", "at", "by", "with", "from", "about", "into", "up",
        "your", "you", "my", "me", "i", "it", "its", "this", "that", "these", "those", "is", "are", "be", "was", "were",
        "now", "today", "tomorrow", "new", "quick", "some", "any", "all", "so", "as", "before", "after", "re", "fwd",
        "do", "get", "make", "take", "send", "reply", "check", "review", "look", "follow", "draft", "write", "read", "add", "set",
    };

    /// <summary>
    /// The candidates that are new: not like anything in <paramref name="earlier"/> and not like a
    /// candidate before them. Order is kept.
    /// </summary>
    public static IReadOnlyList<Suggestion> KeepNew(IEnumerable<Suggestion> candidates, IEnumerable<Suggestion> earlier)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var seen = earlier.Select(Fingerprint.Of).ToList();
        var kept = new List<Suggestion>();
        foreach (var candidate in candidates)
        {
            var print = Fingerprint.Of(candidate);
            if (seen.Any(s => s.SameIdeaAs(print)))
            {
                continue;
            }

            kept.Add(candidate);
            seen.Add(print);
        }

        return kept;
    }

    public static bool IsRepeat(Suggestion candidate, IEnumerable<Suggestion> earlier)
    {
        var print = Fingerprint.Of(candidate);
        return earlier.Any(e => Fingerprint.Of(e).SameIdeaAs(print));
    }

    /// <summary>Lower case, no stop words, plural and -ing/-ed endings trimmed.</summary>
    internal static HashSet<string> Words(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in WordPattern().Matches(text.ToLower(CultureInfo.InvariantCulture)))
        {
            var raw = match.Value.EndsWith("'s", StringComparison.Ordinal) || match.Value.EndsWith("’s", StringComparison.Ordinal) ? match.Value[..^2] : match.Value;
            var word = Stem(raw);
            if (word.Length > 1 && !s_stopWords.Contains(word) && !s_stopWords.Contains(raw))
            {
                words.Add(word);
            }
        }

        return words;
    }

    private static string Stem(string word)
    {
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
        {
            return word[..^3];
        }

        if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal))
        {
            return word[..^2];
        }

        if (word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal))
        {
            return word[..^3] + "y";
        }

        if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal))
        {
            return word[..^1];
        }

        return word;
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’.-]*[\p{L}\p{N}]|[\p{L}\p{N}]", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    private sealed record Fingerprint(HashSet<string> Words, string Topic, HashSet<string> References)
    {
        public static Fingerprint Of(Suggestion s)
        {
            var topic = string.Join(' ', RepeatFilter.Words(s.Topic ?? string.Empty).Order(StringComparer.Ordinal));
            var references = new HashSet<string>(
                (s.Sources ?? []).Select(r => NormalizeReference(r.Reference)).Where(r => r.Length > 0),
                StringComparer.Ordinal);
            var words = RepeatFilter.Words($"{s.Title} {s.Topic}");
            return new Fingerprint(words, topic, references);
        }

        public bool SameIdeaAs(Fingerprint other)
        {
            if (References.Overlaps(other.References))
            {
                return true;
            }

            if (Topic.Length > 0 && Topic == other.Topic)
            {
                return true;
            }

            // Two shared words at least, so "Call Priya" and "Thank Priya" stay different ideas.
            var shared = Words.Count(other.Words.Contains);
            if (shared < 2)
            {
                return false;
            }

            var ofSmaller = (double)shared / Math.Min(Words.Count, other.Words.Count);
            var ofBoth = (double)shared / (Words.Count + other.Words.Count - shared);
            return ofSmaller >= SmallerOverlap || ofBoth >= SameIdeaOverlap;
        }

        private static string NormalizeReference(string? reference)
        {
            var value = (reference ?? string.Empty).Trim().ToLowerInvariant();
            if (value.StartsWith("http", StringComparison.Ordinal))
            {
                value = value.TrimEnd('/');
                var hash = value.IndexOf('#', StringComparison.Ordinal);
                if (hash > 0)
                {
                    value = value[..hash];
                }
            }

            return value;
        }
    }
}
