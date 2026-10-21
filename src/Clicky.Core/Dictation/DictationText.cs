using System.Globalization;
using System.Text.RegularExpressions;

namespace Clicky.Core.Dictation;

/// <summary>Text rules for dictation that need no model: spoken commands, dashes, joining chunks, terminals.</summary>
public static partial class DictationText
{
    /// <summary>
    /// "new line" and "new paragraph" become line breaks. Whisper writes them as words, usually
    /// as a sentence of their own ("Hello. New line. Next."), so the punctuation around them goes too.
    /// </summary>
    public static string ApplySpokenCommands(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = NewParagraph().Replace(text, "\n\n");
        text = NewLine().Replace(text, "\n");
        return CapitalizeAfterBreaks(SpaceAroundBreaks().Replace(text, string.Empty));
    }

    /// <summary>Em and en dashes used as punctuation become commas (the plan's house style: no em dashes).</summary>
    public static string RemoveDashes(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = SpacedDash().Replace(text, ", ");
        text = text.Replace('—', ',').Replace(" ,", ",", StringComparison.Ordinal);
        return DoubledPunctuation().Replace(text, "$1");
    }

    /// <summary>
    /// For terminals: every line break becomes a space, so pasting or typing the text can never
    /// press Enter and run a command.
    /// </summary>
    public static string ForTerminal(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return LineBreaks().Replace(text, " ").Trim();
    }

    /// <summary>
    /// What to insert after text that ends with <paramref name="before"/> (null if unknown): a
    /// space between words, none after a line break, an opening bracket or at the start of a field.
    /// </summary>
    public static string Spaced(string text, char? before)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || before is not { } previous || char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(previous))
        {
            return text;
        }

        if (previous is '(' or '[' or '{' or '"' or '\'' or '/' or '@' or '#' || IsClosingPunctuation(text[0]))
        {
            return text;
        }

        return " " + text;
    }

    /// <summary>Joins two transcribed chunks.</summary>
    public static string Join(string first, string second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.Length == 0)
        {
            return second;
        }

        return first + Spaced(second, first[^1]);
    }

    private static bool IsClosingPunctuation(char c) => c is '.' or ',' or '!' or '?' or ';' or ':' or ')' or ']' or '}';

    private static string CapitalizeAfterBreaks(string text) =>
        AfterBreak().Replace(text, m => m.Groups[1].Value + char.ToUpper(m.Groups[2].Value[0], CultureInfo.CurrentCulture));

    // A command is its own phrase: at the start, after a sentence or a comma, and followed by
    // punctuation or the end. "A new line of products" is left alone.
    [GeneratedRegex(@"(?:[ \t]*,[ \t]*|(?<=(?:^|[.!?;:\n])[ \t]*))\bnew[ \t]+paragraph\b[ \t]*(?:[.,;:!?]+|(?=$|\n))[ \t]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NewParagraph();

    [GeneratedRegex(@"(?:[ \t]*,[ \t]*|(?<=(?:^|[.!?;:\n])[ \t]*))\bnew[ \t]+line\b[ \t]*(?:[.,;:!?]+|(?=$|\n))[ \t]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NewLine();

    [GeneratedRegex(@"[ \t]+(?=\n)|(?<=\n)[ \t]+", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceAroundBreaks();

    [GeneratedRegex(@"(\n)(\p{Ll})", RegexOptions.CultureInvariant)]
    private static partial Regex AfterBreak();

    [GeneratedRegex(@"[ \t]+[—–][ \t]+|(?<=\p{L})[—–](?=\p{L})", RegexOptions.CultureInvariant)]
    private static partial Regex SpacedDash();

    [GeneratedRegex(@"([,.;:!?]),", RegexOptions.CultureInvariant)]
    private static partial Regex DoubledPunctuation();

    [GeneratedRegex(@"[ \t]*(\r\n|\r|\n)+[ \t]*", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreaks();
}
