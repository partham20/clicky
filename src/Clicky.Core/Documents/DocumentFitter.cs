using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Clicky.Core.Assistant;

namespace Clicky.Core.Documents;

/// <param name="IncludedTokens">Estimated tokens of document text sent.</param>
/// <param name="TotalTokens">Estimated tokens of the whole document as read.</param>
public sealed record FittedDocument(RequestAttachment Attachment, int IncludedTokens, int TotalTokens)
{
    public bool IsPartial => IncludedTokens < TotalTokens;

    /// <summary>Share of the document sent, 0 to 1.</summary>
    public double Coverage => TotalTokens == 0 ? 1 : Math.Min(1, (double)IncludedTokens / TotalTokens);
}

/// <summary>
/// Fits documents into one request's token budget. A document that fits goes whole, with page or
/// slide markers so the model can cite them. One that doesn't is cut into chunks of about
/// <see cref="ChunkTokens"/> along page, paragraph and line boundaries; the start and the end are
/// always kept (titles, summaries and conclusions live there), then the chunks that share the most
/// rare words with the question, then an even spread over the rest, until the budget is used. The
/// chosen chunks go in document order with every gap marked, and a note tells the model exactly
/// which parts it has, so it can say when the answer may be in a part it can't see.
/// </summary>
/// <remarks>
/// Picking by word overlap rather than asking a model keeps text mode at one round trip, and is
/// deterministic. "Summarize this" has no useful words, so it gets the even spread.
/// </remarks>
public static partial class DocumentFitter
{
    public const int ChunkTokens = 1_500;

    /// <summary>Kept back from a partial document's budget for its note and gap markers.</summary>
    public const int NoteAllowance = 250;

    private const int GapMarkerTokens = 12;

    private static readonly HashSet<string> s_stopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "but", "not", "you", "all", "any", "can", "had", "her", "was", "one", "our", "out", "has", "have", "his",
        "how", "its", "may", "who", "did", "does", "get", "got", "let", "say", "she", "too", "use", "what", "when", "where", "which", "while",
        "with", "this", "that", "these", "those", "there", "their", "them", "then", "than", "they", "from", "into", "about", "would", "could",
        "should", "will", "your", "yours", "just", "like", "also", "some", "more", "most", "much", "many", "very", "here", "only", "over",
        "such", "each", "other", "been", "being", "were", "why", "tell", "please", "document", "file", "page", "pages", "doc", "pdf", "says",
        "said", "explain", "summarize", "summarise", "summary", "read", "text", "find", "show", "give", "know", "want", "need", "make",
    };

    /// <summary>Fits one document into <paramref name="maxTokens"/>.</summary>
    /// <param name="question">The user's question, to pick the relevant parts of a long document; may be empty.</param>
    public static FittedDocument Fit(DocumentContent document, int maxTokens, string? question)
    {
        ArgumentNullException.ThrowIfNull(document);
        maxTokens = Math.Max(maxTokens, 1);
        var total = document.EstimatedTokens;
        var origin = document.Origin;

        if (document.IsEmpty)
        {
            var empty = document.Warning ?? "Clicky found no text in this document.";
            return new(RequestAttachment.Document(document.Name, string.Empty, empty, origin), 0, 0);
        }

        var whole = RenderWhole(document);
        var wholeTokens = TokenEstimate.Of(whole);
        if (wholeTokens + TokenEstimate.Of(document.Warning) <= maxTokens)
        {
            return new(RequestAttachment.Document(document.Name, whole, document.Warning, origin), total, total);
        }

        var chunks = Split(document);
        var chosen = Choose(chunks, Math.Max(maxTokens - NoteAllowance, 1), question);
        var (text, included) = Render(document, chunks, chosen, Math.Max(maxTokens - NoteAllowance, 1));
        var note = Note(document, chunks, chosen, included, total);
        return new(RequestAttachment.Document(document.Name, text, note, origin), Math.Min(included, total), total);
    }

    /// <summary>
    /// Fits several documents into one budget: small ones go whole and the rest share what is left
    /// equally, so one huge file can't crowd out the others.
    /// </summary>
    public static IReadOnlyList<FittedDocument> FitAll(IReadOnlyList<DocumentContent> documents, int totalTokens, string? question)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var sizes = documents.Select(d => TokenEstimate.Of(RenderWhole(d)) + TokenEstimate.Of(d.Warning)).ToArray();
        var shares = Shares(sizes, totalTokens);
        return [.. documents.Select((d, i) => Fit(d, shares[i], question))];
    }

    /// <summary>Water-filling: each size gets at most an equal share of what the smaller ones left.</summary>
    internal static int[] Shares(IReadOnlyList<int> sizes, int total)
    {
        var shares = new int[sizes.Count];
        var remaining = Math.Max(total, 0);
        var order = Enumerable.Range(0, sizes.Count).OrderBy(i => sizes[i]).ToArray();
        for (var k = 0; k < order.Length; k++)
        {
            var share = remaining / (order.Length - k);
            var given = Math.Min(sizes[order[k]], share);
            shares[order[k]] = given;
            remaining -= given;
        }

        return shares;
    }

    /// <summary>The whole document, with a marker before every page or slide.</summary>
    internal static string RenderWhole(DocumentContent document)
    {
        if (document.Unit is null)
        {
            return string.Join("\n\n", document.Sections.Select(s => s.Text.Trim()).Where(t => t.Length > 0));
        }

        var text = new StringBuilder();
        foreach (var section in document.Sections)
        {
            if (text.Length > 0)
            {
                text.Append("\n\n");
            }

            text.Append(Marker(document.Unit, section.Number, continued: false)).Append('\n').Append(section.Text.Trim());
        }

        return text.ToString();
    }

    internal static IReadOnlyList<Chunk> Split(DocumentContent document, int chunkTokens = ChunkTokens)
    {
        var chunks = new List<Chunk>();
        foreach (var section in document.Sections)
        {
            var first = true;
            var current = new StringBuilder();
            var tokens = 0;

            void Flush()
            {
                var text = current.ToString().Trim();
                if (text.Length > 0)
                {
                    chunks.Add(new Chunk(chunks.Count, section.Number, text, first, TokenEstimate.Of(text)));
                    first = false;
                }

                current.Clear();
                tokens = 0;
            }

            foreach (var line in Pieces(section.Text, chunkTokens))
            {
                var lineTokens = TokenEstimate.Of(line) + 1;
                if (tokens > 0 && tokens + lineTokens > chunkTokens)
                {
                    Flush();
                }

                current.Append(line).Append('\n');
                tokens += lineTokens;
            }

            Flush();
        }

        return chunks;
    }

    /// <summary>Indexes of the chunks to send, in document order.</summary>
    internal static IReadOnlyList<int> Choose(IReadOnlyList<Chunk> chunks, int budget, string? question)
    {
        if (chunks.Count == 0)
        {
            return [];
        }

        var scores = Score(chunks, question);
        var spread = BisectionRanks(chunks.Count);
        var candidates = Enumerable.Range(0, chunks.Count)
            .OrderByDescending(i => i == 0)
            .ThenByDescending(i => i == chunks.Count - 1)
            .ThenByDescending(i => scores[i])
            .ThenBy(i => spread[i]);

        var chosen = new SortedSet<int>();
        var used = 0;
        foreach (var i in candidates)
        {
            var cost = chunks[i].Tokens + GapMarkerTokens;
            if (used + cost <= budget)
            {
                chosen.Add(i);
                used += cost;
            }
        }

        // Not even the first chunk fits: Render cuts it to the budget.
        if (chosen.Count == 0)
        {
            chosen.Add(0);
        }

        return [.. chosen];
    }

    /// <summary>How well each chunk matches the question: summed log term frequency times inverse document frequency.</summary>
    internal static double[] Score(IReadOnlyList<Chunk> chunks, string? question)
    {
        var scores = new double[chunks.Count];
        var terms = Terms(question ?? string.Empty).Distinct().ToArray();
        if (terms.Length == 0)
        {
            return scores;
        }

        var wanted = terms.ToHashSet(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>[chunks.Count];
        var frequency = terms.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        for (var i = 0; i < chunks.Count; i++)
        {
            counts[i] = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var term in Terms(chunks[i].Text))
            {
                if (wanted.Contains(term))
                {
                    counts[i][term] = counts[i].GetValueOrDefault(term) + 1;
                }
            }

            foreach (var term in counts[i].Keys)
            {
                frequency[term]++;
            }
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            foreach (var (term, count) in counts[i])
            {
                scores[i] += (1 + Math.Log(count)) * Math.Log(1 + ((double)chunks.Count / frequency[term]));
            }
        }

        return scores;
    }

    /// <summary>Middle first, then the middles of each half, and so on: an even spread for any prefix.</summary>
    internal static int[] BisectionRanks(int count)
    {
        var ranks = new int[count];
        var queue = new Queue<(int Low, int High)>();
        queue.Enqueue((0, count - 1));
        var rank = 0;
        while (queue.Count > 0)
        {
            var (low, high) = queue.Dequeue();
            if (low > high)
            {
                continue;
            }

            var middle = low + ((high - low) / 2);
            ranks[middle] = rank++;
            queue.Enqueue((low, middle - 1));
            queue.Enqueue((middle + 1, high));
        }

        return ranks;
    }

    internal static IEnumerable<string> Terms(string text)
    {
        foreach (Match match in WordPattern().Matches(text))
        {
            var word = match.Value.ToLowerInvariant();
            if (word.Length < 3 || s_stopWords.Contains(word))
            {
                continue;
            }

            // Crude stemming so "invoices" finds "invoice".
            yield return word.Length > 4 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) ? word[..^1] : word;
        }
    }

    private static (string Text, int Tokens) Render(DocumentContent document, IReadOnlyList<Chunk> chunks, IReadOnlyList<int> chosen, int budget)
    {
        var text = new StringBuilder();
        var previous = -1;
        foreach (var i in chosen)
        {
            if (i > previous + 1)
            {
                text.Append(text.Length > 0 ? "\n\n" : string.Empty).Append(GapMarker(document, chunks, previous + 1, i - 1, chosen)).Append("\n\n");
            }
            else if (text.Length > 0)
            {
                text.Append("\n\n");
            }

            var chunk = chunks[i];
            if (document.Unit is not null && (chunk.StartsSection || i != previous + 1))
            {
                text.Append(Marker(document.Unit, chunk.Number, continued: !chunk.StartsSection)).Append('\n');
            }

            text.Append(chunk.Text);
            previous = i;
        }

        if (previous < chunks.Count - 1)
        {
            text.Append("\n\n").Append(GapMarker(document, chunks, previous + 1, chunks.Count - 1, chosen));
        }

        var rendered = text.ToString();
        if (TokenEstimate.Of(rendered) > budget)
        {
            // Only when a single chunk is over the budget (many documents sharing a small one).
            rendered = rendered[..Math.Min(rendered.Length, TokenEstimate.CharsFor(budget))] + "\n[... the rest is left out ...]";
        }

        return (rendered, chosen.Sum(i => chunks[i].Tokens));
    }

    private static string Marker(string unit, int? number, bool continued) =>
        number is { } n ? string.Create(CultureInfo.InvariantCulture, $"--- {unit} {n}{(continued ? " (continued)" : string.Empty)} ---") : "---";

    private static string GapMarker(DocumentContent document, IReadOnlyList<Chunk> chunks, int from, int to, IReadOnlyList<int> chosen)
    {
        if (document.Unit is not null)
        {
            var kept = chosen.Select(i => chunks[i].Number).ToHashSet();
            var numbers = Enumerable.Range(from, to - from + 1).Select(i => chunks[i].Number).OfType<int>().Distinct().ToList();
            var whole = numbers.Where(n => !kept.Contains(n)).ToList();
            var parts = numbers.Where(n => kept.Contains(n)).ToList();
            var described = new List<string>();
            if (whole.Count > 0)
            {
                described.Add(Ranges(document.Unit, whole));
            }

            described.AddRange(parts.Select(n => string.Create(CultureInfo.InvariantCulture, $"part of {document.Unit} {n}")));
            return $"[... left out: {string.Join(", ", described)} ...]";
        }

        var words = Enumerable.Range(from, to - from + 1).Sum(i => WordCount(chunks[i].Text));
        return string.Create(CultureInfo.InvariantCulture, $"[... about {Round(words):N0} words left out ...]");
    }

    private static string Note(DocumentContent document, IReadOnlyList<Chunk> chunks, IReadOnlyList<int> chosen, int included, int total)
    {
        var note = new StringBuilder();
        var percent = total == 0 ? 100 : Math.Clamp((int)Math.Round(100.0 * included / total), 1, 99);
        note.Append(CultureInfo.InvariantCulture, $"This document is too long to send whole, so only about {percent}% of it is included. ");
        if (document.Unit is not null)
        {
            var numbers = chosen.Select(i => chunks[i].Number).OfType<int>().Distinct().Order().ToList();
            var of = document.TotalUnits is { } units ? string.Create(CultureInfo.InvariantCulture, $" of {units}") : string.Empty;
            note.Append(CultureInfo.InvariantCulture, $"Included: {Ranges(document.Unit, numbers)}{of}, some only in part. ");
        }
        else
        {
            note.Append("Included: the start, the end, and the parts that best match the question, in order. ");
        }

        note.Append("Gaps are marked [... left out ...].");
        if (document.Warning is { } warning)
        {
            note.Append(' ').Append(warning);
        }

        return note.ToString();
    }

    /// <summary>"pages 1-3, 7, 9-12" (or "page 4").</summary>
    internal static string Ranges(string unit, IReadOnlyList<int> numbers)
    {
        var sorted = numbers.Distinct().Order().ToList();
        if (sorted.Count == 0)
        {
            return $"no {unit}s";
        }

        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
            {
                j++;
            }

            parts.Add(i == j ? sorted[i].ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{sorted[i]}-{sorted[j]}"));
            i = j + 1;
        }

        return $"{unit}{(sorted.Count > 1 ? "s" : string.Empty)} {string.Join(", ", parts)}";
    }

    /// <summary>Lines, with any line longer than a chunk cut at spaces.</summary>
    private static IEnumerable<string> Pieces(string text, int chunkTokens)
    {
        var maxChars = TokenEstimate.CharsFor(chunkTokens) / 2;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (TokenEstimate.Of(line) <= chunkTokens / 2)
            {
                yield return line;
                continue;
            }

            var start = 0;
            while (start < line.Length)
            {
                var length = Math.Min(maxChars, line.Length - start);
                if (start + length < line.Length)
                {
                    var space = line.LastIndexOf(' ', start + length - 1, length);
                    if (space > start + (length / 2))
                    {
                        length = space - start + 1;
                    }
                }

                yield return line.Substring(start, length).Trim();
                start += length;
            }
        }
    }

    private static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static int Round(int words) => words < 100 ? words : (int)(Math.Round(words / 100.0) * 100);

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    /// <param name="StartsSection">The first chunk of its page or slide.</param>
    internal sealed record Chunk(int Index, int? Number, string Text, bool StartsSection, int Tokens);
}
