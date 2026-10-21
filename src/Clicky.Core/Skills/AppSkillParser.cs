namespace Clicky.Core.Skills;

/// <summary>
/// Reads an app skill file: Markdown with a small front-matter header.
/// </summary>
/// <remarks>
/// <code>
/// ---
/// name: Excel
/// apps: excel, et
/// titles: [ "- Excel" ]
/// description: Ribbon layout and common tasks in Excel 365
/// ---
/// The Data tab has Sort and Filter. Freeze Panes is under View...
/// </code>
/// <para>Only the keys above are read; list values may be comma-separated, a [bracketed] list, or a
/// YAML block list ("- item" lines). A skill needs <c>apps</c> or <c>titles</c>, or it can never match.
/// <c>name</c> defaults to the file name, or the folder name for <c>&lt;folder&gt;\SKILL.md</c>.</para>
/// </remarks>
public static class AppSkillParser
{
    /// <summary>Longest body sent to the model (about 3,000 tokens); the rest is cut.</summary>
    public const int MaxBodyChars = 12_000;

    private const string Fence = "---";

    /// <param name="defaultName">Name to use when the header has none.</param>
    /// <param name="error">Why the file is not a usable skill, for the log.</param>
    public static bool TryParse(string text, string path, string defaultName, out AppSkill? skill, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        skill = null;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿').Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != Fence)
        {
            error = "no front matter (the file must start with a --- line)";
            return false;
        }

        var end = Array.FindIndex(lines, 1, l => l.Trim() == Fence);
        if (end < 0)
        {
            error = "front matter is not closed with a --- line";
            return false;
        }

        var fields = ReadFields(lines.AsSpan(1, end - 1));
        var apps = List(fields, "apps").Select(StripExe).ToArray();
        var titles = List(fields, "titles");
        if (apps.Length == 0 && titles.Count == 0)
        {
            error = "no apps or titles, so it would never be used";
            return false;
        }

        var body = string.Join('\n', lines.AsSpan(end + 1).ToArray()).Trim();
        if (body.Length == 0)
        {
            error = "the guidance below the front matter is empty";
            return false;
        }

        if (body.Length > MaxBodyChars)
        {
            body = string.Concat(body.AsSpan(0, MaxBodyChars).TrimEnd(), "\n[...the rest of this guide was cut]");
        }

        var name = Scalar(fields, "name");
        skill = new AppSkill(
            string.IsNullOrWhiteSpace(name) ? defaultName : name,
            Scalar(fields, "description"),
            apps,
            titles,
            body,
            path);
        error = null;
        return true;
    }

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    // Key -> raw values: one for "key: value", several for a following "- item" block.
    private static Dictionary<string, List<string>> ReadFields(ReadOnlySpan<string> lines)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) && current is not null && line.Length > trimmed.Length)
            {
                current.Add(trimmed[2..]);
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
            {
                current = null;
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            current = [];
            if (value.Length > 0)
            {
                current.Add(value);
            }

            fields[key] = current;
        }

        return fields;
    }

    private static string? Scalar(Dictionary<string, List<string>> fields, string key) =>
        fields.TryGetValue(key, out var values) && values.Count > 0 ? Unquote(string.Join(' ', values)) : null;

    private static List<string> List(Dictionary<string, List<string>> fields, string key)
    {
        var items = new List<string>();
        if (!fields.TryGetValue(key, out var values))
        {
            return items;
        }

        foreach (var value in values)
        {
            var text = value.Trim();
            if (text.StartsWith('[') && text.EndsWith(']'))
            {
                text = text[1..^1];
            }

            // Commas inside quotes stay: titles: "Inbox, Mail", Outlook
            foreach (var part in SplitList(text))
            {
                var item = Unquote(part.Trim());
                if (item.Length > 0 && !items.Contains(item, StringComparer.OrdinalIgnoreCase))
                {
                    items.Add(item);
                }
            }
        }

        return items;
    }

    // Splits on commas outside quotes.
    private static List<string> SplitList(string text)
    {
        var parts = new List<string>();
        var start = 0;
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is { } q)
            {
                if (c == q)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == ',')
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    private static bool IsQuoted(string text) =>
        text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\''));

    private static string Unquote(string text)
    {
        text = text.Trim();
        return IsQuoted(text) ? text[1..^1] : text;
    }
}
