using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Clicky.Core.Voice;

/// <summary>
/// Turns one sentence of reply text (which may carry Markdown) into what a voice should say: no
/// asterisks, link syntax, list bullets, table pipes or emoji names. Pairs with
/// <see cref="SentenceSplitter"/>, which has already dropped code blocks.
/// </summary>
public static partial class SpeakableText
{
    /// <summary>The words to speak, or empty if there is nothing worth saying.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var s = text;
        s = Image().Replace(s, "$1");
        s = Link().Replace(s, "$1");
        s = AngleUrl().Replace(s, "$1");
        s = Url().Replace(s, m => HostOf(m.Value));
        s = HtmlTag().Replace(s, " ");
        s = InlineCode().Replace(s, "$1");
        s = HorizontalRule().Replace(s, string.Empty);
        s = TableSeparator().Replace(s, string.Empty);

        // "> - item" carries two markers.
        for (var i = 0; i < 3; i++)
        {
            s = LineMarker().Replace(s, string.Empty);
        }

        if (s.Contains('|', StringComparison.Ordinal))
        {
            s = string.Join(", ", s.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(c => c.Length > 0));
        }

        s = Strong().Replace(s, "$2");
        s = Strike().Replace(s, "$1");
        s = Emphasis().Replace(s, "$1");
        s = s.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
        s = WithoutSymbols(s);
        s = Whitespace().Replace(s, " ").Trim();

        return s.Any(char.IsLetterOrDigit) ? s : string.Empty;
    }

    /// <summary>"https://www.docs.claude.com/en/api?x=1" is read as "docs.claude.com".</summary>
    private static string HostOf(string url)
    {
        var trimmed = url.TrimEnd('.', ',', ';', ':', '!', '?');
        var trailing = url[trimmed.Length..];
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return url;
        }

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return host + trailing;
    }

    /// <summary>
    /// Drops emoji and other pictographs, which Windows voices read out by name ("grinning face"),
    /// with their joiners and variation selectors.
    /// </summary>
    private static string WithoutSymbols(string s)
    {
        var builder = new StringBuilder(s.Length);
        foreach (var rune in s.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.OtherSymbol
                || rune.Value is 0x200D or 0x20E3 || (rune.Value >= 0xFE00 && rune.Value <= 0xFE0F)
                || (rune.Value >= 0x1F3FB && rune.Value <= 0x1F3FF))
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\((?:[^()\s]|\([^)]*\))*(?:\s+""[^""]*"")?\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"<(https?://[^>\s]+)>")]
    private static partial Regex AngleUrl();

    [GeneratedRegex(@"\bhttps?://[^\s<>()\[\]""']+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"</?[A-Za-z][A-Za-z0-9-]*(?:\s[^<>]*)?/?>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"`+([^`]*)`+")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"^\s{0,3}(?:#{1,6}\s+|>\s?|[-*+•]\s+|\d{1,3}[.)]\s+|\[[ xX]\]\s+)")]
    private static partial Regex LineMarker();

    [GeneratedRegex(@"(\*\*|__)(.+?)\1")]
    private static partial Regex Strong();

    [GeneratedRegex(@"~~(.+?)~~")]
    private static partial Regex Strike();

    [GeneratedRegex(@"(?<![\w*])[*_](?=\S)(.+?)(?<=\S)[*_](?![\w*])")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
