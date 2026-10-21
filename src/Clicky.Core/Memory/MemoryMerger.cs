using System.Text.RegularExpressions;

namespace Clicky.Core.Memory;

/// <summary>Both memory files as parsed documents.</summary>
public sealed record MemorySet(MemoryDocument Profile, MemoryDocument Volatile)
{
    public MemoryDocument this[MemoryFile file] => file == MemoryFile.Profile ? Profile : Volatile;

    public MemorySet With(MemoryFile file, MemoryDocument document) =>
        file == MemoryFile.Profile ? this with { Profile = document } : this with { Volatile = document };
}

public sealed record RejectedEdit(MemoryEdit Edit, string Reason);

public sealed record MergeResult(MemorySet Memory, IReadOnlyList<MemoryEdit> Applied, IReadOnlyList<RejectedEdit> Rejected)
{
    public bool Changed(MemoryFile file) => Applied.Any(e => e.File == file);
}

/// <summary>
/// Applies proposed edits under fixed rules, so a confused model can't wreck the files:
/// <list type="bullet">
/// <item>Entries match by text, ignoring case, spacing and end punctuation.</item>
/// <item>An add that repeats an existing entry anywhere in the file is a no-op, not a duplicate.</item>
/// <item>Replace/remove need the old entry to exist; a guess at it is refused, never fuzzily applied.</item>
/// <item>Nothing is ever shortened. An entry over the entry cap, or an edit that would push the file
/// past its cap, is refused whole; the updater is told the remaining room so it consolidates instead.</item>
/// <item>Anything that looks like a secret (API keys, tokens, passwords) is refused.</item>
/// <item>At most <see cref="MemoryLimits.EditsPerPass"/> edits per pass.</item>
/// </list>
/// </summary>
public static partial class MemoryMerger
{
    public static MergeResult Apply(MemorySet memory, IEnumerable<MemoryEdit> edits, MemoryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(limits);

        var applied = new List<MemoryEdit>();
        var rejected = new List<RejectedEdit>();

        foreach (var edit in edits)
        {
            if (applied.Count >= limits.EditsPerPass)
            {
                rejected.Add(new(edit, "too many edits in one pass"));
                continue;
            }

            var (next, reason) = ApplyOne(memory[edit.File], edit, limits);
            if (reason is not null)
            {
                rejected.Add(new(edit, reason));
                continue;
            }

            if (next is not null)
            {
                memory = memory.With(edit.File, next);
                applied.Add(edit);
            }
        }

        return new MergeResult(memory, applied, rejected);
    }

    /// <summary>Body size as counted against the caps: the sections, not the preamble.</summary>
    public static int BodyLength(MemoryDocument document) => document.BodyMarkdown().Length;

    /// <summary>Matching key: lower case, single spaces, no trailing punctuation or bullet.</summary>
    public static string Normalize(string text) =>
        Whitespace().Replace(text, " ").Trim().TrimEnd('.', '!', ';', ',').TrimStart('-', '*', ' ').ToLowerInvariant();

    public static bool LooksSecret(string text) => SecretPattern().IsMatch(text);

    /// <returns>The new document, null for a no-op, or a refusal reason.</returns>
    private static (MemoryDocument? Next, string? Reason) ApplyOne(MemoryDocument doc, MemoryEdit edit, MemoryLimits limits)
    {
        if (edit.Text is { } text)
        {
            if (text.Length > limits.EntryChars)
            {
                return (null, $"entry longer than {limits.EntryChars} characters");
            }

            if (text.Contains('\n', StringComparison.Ordinal))
            {
                return (null, "entry spans several lines");
            }

            if (LooksSecret(text))
            {
                return (null, "looks like a secret");
            }
        }

        var sections = doc.Sections.ToList();
        var index = sections.FindIndex(s => s.HasHeading(edit.Section));
        MemoryDocument next;

        switch (edit.Kind)
        {
            case MemoryEditKind.Add:
            {
                var key = Normalize(edit.Text!);
                if (doc.Sections.SelectMany(s => s.Entries).Any(e => Normalize(e.Text) == key))
                {
                    return (null, null);
                }

                if (index < 0)
                {
                    sections.Add(new MemorySection(edit.Section, [new MemoryEntry(edit.Text!)]));
                }
                else
                {
                    sections[index] = sections[index] with { Entries = [.. sections[index].Entries, new MemoryEntry(edit.Text!)] };
                }

                next = doc with { Sections = sections };
                break;
            }

            case MemoryEditKind.Replace:
            case MemoryEditKind.Remove:
            {
                var (sectionIndex, entryIndex) = FindEntry(sections, index, edit.Old!);
                if (entryIndex < 0)
                {
                    return (null, "the entry to change isn't in memory");
                }

                var entries = sections[sectionIndex].Entries.ToList();
                if (edit.Kind == MemoryEditKind.Remove)
                {
                    entries.RemoveAt(entryIndex);
                }
                else if (Normalize(entries[entryIndex].Text) == Normalize(edit.Text!))
                {
                    return (null, null);
                }
                else
                {
                    entries[entryIndex] = new MemoryEntry(edit.Text!);
                }

                sections[sectionIndex] = sections[sectionIndex] with { Entries = entries };
                next = doc with { Sections = sections };
                break;
            }

            default:
                return (null, "unknown edit");
        }

        // Only growth is capped: a hand-edited file that is already over must still accept removals.
        var cap = limits.CapFor(edit.File);
        var size = BodyLength(next);
        if (size > cap && size > BodyLength(doc))
        {
            return (null, $"{MemoryTemplates.FileName(edit.File)} would exceed {cap} characters");
        }

        return (next, null);
    }

    // The named section first; the model sometimes names the wrong one, so then the rest of the file.
    private static (int Section, int Entry) FindEntry(List<MemorySection> sections, int preferred, string old)
    {
        var key = Normalize(old);
        var order = preferred >= 0 ? new[] { preferred }.Concat(Enumerable.Range(0, sections.Count).Where(i => i != preferred)) : Enumerable.Range(0, sections.Count);
        foreach (var s in order)
        {
            var entries = sections[s].Entries;
            for (var e = 0; e < entries.Count; e++)
            {
                if (Normalize(entries[e].Text) == key)
                {
                    return (s, e);
                }
            }
        }

        return (-1, -1);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Key prefixes (Anthropic, OpenAI, GitHub, Slack, AWS, Google), bearer/JWT tokens, private keys,
    // "password: x" style lines, and long unbroken base64/hex runs.
    [GeneratedRegex(
        @"\b(sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[abpr]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,})" +
        @"|\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}" +
        @"|-----BEGIN [A-Z ]*PRIVATE KEY-----" +
        @"|\b(password|passwd|passcode|pin|otp|api[ _-]?key|secret|token)\b\s*(is|:|=)\s*\S+" +
        @"|[A-Za-z0-9+/_=-]{40,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}
