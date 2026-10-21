using System.Text;

namespace Clicky.Core.Memory;

/// <summary>The two memory files in %AppData%\Clicky\memory.</summary>
public enum MemoryFile
{
    /// <summary>PROFILE.md: who the user is, habits, and how they want Clicky to behave. Long-lived.</summary>
    Profile,

    /// <summary>VOLATILE.md: what they are working on right now. Expected to churn.</summary>
    Volatile,
}

/// <summary>One line of memory under a section heading. Multi-line when a hand edit wrapped it.</summary>
/// <param name="Text">The entry without its bullet marker.</param>
/// <param name="IsBullet">False for free text a person typed without "- "; kept as-is on write.</param>
public sealed record MemoryEntry(string Text, bool IsBullet = true);

/// <summary>A "## Heading" and the entries under it.</summary>
public sealed record MemorySection(string Heading, IReadOnlyList<MemoryEntry> Entries)
{
    public bool HasHeading(string heading) => string.Equals(Heading, heading.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A memory file parsed into sections. Parsing is lossless for anything a person is likely to type:
/// text before the first "##" is kept verbatim as the preamble, bullets ("- " or "* ") become entries,
/// indented lines continue the entry above, and any other line is kept as a free-text entry. Blank
/// lines between entries are not preserved.
/// </summary>
public sealed record MemoryDocument(string Preamble, IReadOnlyList<MemorySection> Sections)
{
    public static MemoryDocument Empty { get; } = new(string.Empty, []);

    public bool IsEmpty => Sections.All(s => s.Entries.Count == 0);

    public int EntryCount => Sections.Sum(s => s.Entries.Count);

    public MemorySection? Find(string heading) => Sections.FirstOrDefault(s => s.HasHeading(heading));

    public static MemoryDocument Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return Empty;
        }

        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var preamble = new StringBuilder();
        var sections = new List<MemorySection>();
        string? heading = null;
        var entries = new List<MemoryEntry>();
        var inComment = false;

        void Flush()
        {
            if (heading is not null)
            {
                sections.Add(new MemorySection(heading, [.. entries]));
            }

            entries.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            // A "## " inside an HTML comment (the file's instructions) is not a section.
            if (!inComment && trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                heading = trimmed[3..].Trim();
                continue;
            }

            if (heading is null)
            {
                preamble.Append(line).Append('\n');
                inComment = TracksComment(inComment, line);
                continue;
            }

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                entries.Add(new MemoryEntry(trimmed[2..].Trim()));
            }
            else if (trimmed is "-" or "*")
            {
                // An empty bullet left behind by an edit.
            }
            else if (line.Length > trimmed.Length && entries.Count > 0)
            {
                var last = entries[^1];
                entries[^1] = last with { Text = last.Text + "\n" + trimmed };
            }
            else
            {
                entries.Add(new MemoryEntry(trimmed, IsBullet: false));
            }
        }

        Flush();
        return new MemoryDocument(preamble.ToString().Trim('\n'), sections);
    }

    /// <summary>Markdown for the file: preamble, then each section with one entry per bullet.</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        if (Preamble.Length > 0)
        {
            sb.Append(Preamble).Append("\n\n");
        }

        foreach (var section in Sections)
        {
            sb.Append("## ").Append(section.Heading).Append('\n');
            AppendEntries(sb, section);
            sb.Append('\n');
        }

        return sb.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>The sections without the preamble, as the model sees them. Empty sections are left out.</summary>
    public string BodyMarkdown() => string.Join("\n", Sections.Where(s => s.Entries.Count > 0).Select(SectionMarkdown)).TrimEnd('\n');

    public static string SectionMarkdown(MemorySection section)
    {
        var sb = new StringBuilder();
        sb.Append("## ").Append(section.Heading).Append('\n');
        AppendEntries(sb, section);
        return sb.ToString();
    }

    private static void AppendEntries(StringBuilder sb, MemorySection section)
    {
        foreach (var entry in section.Entries)
        {
            var parts = entry.Text.Split('\n');
            sb.Append(entry.IsBullet ? "- " : string.Empty).Append(parts[0]).Append('\n');
            foreach (var continuation in parts.Skip(1))
            {
                sb.Append("  ").Append(continuation).Append('\n');
            }
        }
    }

    private static bool TracksComment(bool inComment, string line)
    {
        var open = line.LastIndexOf("<!--", StringComparison.Ordinal);
        var close = line.LastIndexOf("-->", StringComparison.Ordinal);
        return open >= 0 || close >= 0 ? open > close : inComment;
    }
}
