using System.Text;
using System.Text.RegularExpressions;

namespace Clicky.Core.Voice;

/// <summary>
/// Cuts streaming reply text into sentences as soon as each one is complete, so the first can be
/// spoken while the rest is still arriving. Feed it chunks of any size (single tokens are fine); the
/// output does not depend on how the text was chunked.
/// </summary>
/// <remarks>
/// <para>A sentence ends at ". ", "! ", "? " (closing quotes and brackets included) and at every
/// line break, so list items and headings are spoken one by one. Not at decimals ("3.14"), versions
/// ("v1.2"), domains ("docs.claude.com"), abbreviations ("e.g.", "Dr."), single initials or list
/// numbers ("1. Open"). "etc." and an ellipsis only end a sentence when the next word is
/// capitalized, which can need one more chunk to tell.</para>
/// <para>Fenced code blocks are dropped whole, and nothing splits inside `inline code`. A run longer
/// than <see cref="SoftLimit"/> characters with no full stop is cut at its next comma, semicolon or
/// colon, so a long sentence doesn't hold up the voice.</para>
/// <para>Output is raw text; <see cref="SpeakableText.Clean"/> turns it into what is spoken.</para>
/// </remarks>
public sealed partial class SentenceSplitter
{
    public const int SoftLimit = 200;

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "e.g", "i.e", "eg", "ie", "approx", "fig", "no", "vol", "cf", "inc", "ltd", "co", "dept", "est", "min", "max", "ca",
    };

    private readonly StringBuilder _text = new();
    private readonly List<string> _out = [];

    private bool _inFence;

    // In _text: where the current line starts, and how many backticks it has had (odd = inside inline code).
    private int _lineStart;
    private int _backticks;

    // Fence lines are skipped up to their newline.
    private bool _skippingLine;

    // Set while the line so far is only spaces, or a prefix of a fence marker.
    private bool _lineMayBeFence = true;

    // A ". " or "... " whose fate depends on the next non-space character, and where it would cut.
    private int _deferredCut = -1;

    /// <summary>Adds text; returns the sentences it completed, in order.</summary>
    public IReadOnlyList<string> Append(string? text)
    {
        _out.Clear();
        foreach (var c in text ?? string.Empty)
        {
            Feed(c);
        }

        return [.. _out];
    }

    /// <summary>Ends the stream: returns whatever is left as the last sentence (or nothing). The splitter can then be reused.</summary>
    public IReadOnlyList<string> Complete()
    {
        _out.Clear();
        if (!_inFence)
        {
            Cut(_text.Length);
        }

        _text.Clear();
        _inFence = false;
        _skippingLine = false;
        _lineStart = 0;
        _backticks = 0;
        _lineMayBeFence = true;
        _deferredCut = -1;
        return [.. _out];
    }

    private static bool IsTerminal(char c) => c is '.' or '!' or '?' or '…';

    private static bool IsCloser(char c) => c is '"' or '\'' or ')' or ']' or '”' or '’' or '*' or '_';

    private void Feed(char c)
    {
        if (c == '\r')
        {
            return;
        }

        if (_skippingLine || _inFence)
        {
            FeedSkipped(c);
            return;
        }

        if (c == '\n')
        {
            _deferredCut = -1;
            Cut(_text.Length);
            NewLine();
            return;
        }

        if (_deferredCut >= 0 && !char.IsWhiteSpace(c))
        {
            // "etc. The" or "Hmm... Let": a new sentence starts with a capital or a digit.
            if (char.IsUpper(c) || char.IsDigit(c))
            {
                Cut(_deferredCut);
            }

            _deferredCut = -1;
        }

        _text.Append(c);
        if (c == '`')
        {
            _backticks++;
        }

        if (_lineMayBeFence)
        {
            var line = _text.ToString(_lineStart, _text.Length - _lineStart).TrimStart();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                // Speak what came before the code, then drop the block.
                _text.Length = _lineStart;
                Cut(_text.Length);
                _inFence = true;
                _skippingLine = true;
                return;
            }

            _lineMayBeFence = line.Length == 0 || "```".StartsWith(line, StringComparison.Ordinal) || "~~~".StartsWith(line, StringComparison.Ordinal);
        }

        if (char.IsWhiteSpace(c) && _text.Length >= 2 && !char.IsWhiteSpace(_text[^2]) && _backticks % 2 == 0)
        {
            OnWordEnd();
        }
    }

    private void FeedSkipped(char c)
    {
        if (c == '\n')
        {
            // The end of a fence line (with its language tag), or of a line of code.
            _skippingLine = false;
            NewLine();
            return;
        }

        if (_skippingLine)
        {
            return;
        }

        // Inside the block: only watch for the closing fence at the start of a line.
        if (!_lineMayBeFence)
        {
            return;
        }

        _text.Append(c);
        var line = _text.ToString().TrimStart();
        if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
        {
            _inFence = false;
            _skippingLine = true;
            _text.Clear();
            return;
        }

        _lineMayBeFence = line.Length == 0 || "```".StartsWith(line, StringComparison.Ordinal) || "~~~".StartsWith(line, StringComparison.Ordinal);
    }

    /// <summary>A space just followed a non-space: decide whether the word before it ended a sentence.</summary>
    private void OnWordEnd()
    {
        var end = _text.Length - 1;

        // Step back over closing quotes and brackets, then the terminal punctuation.
        var i = end - 1;
        while (i >= _lineStart && IsCloser(_text[i]))
        {
            i--;
        }

        var punctuationEnd = i;
        while (i >= _lineStart && IsTerminal(_text[i]))
        {
            i--;
        }

        var punctuation = _text.ToString(i + 1, punctuationEnd - i);
        if (punctuation.Length == 0)
        {
            if (end - _lineStart >= SoftLimit && punctuationEnd >= 0 && _text[punctuationEnd] is ',' or ';' or ':')
            {
                Cut(end);
            }

            return;
        }

        if (punctuation.Contains('!', StringComparison.Ordinal) || punctuation.Contains('?', StringComparison.Ordinal))
        {
            Cut(end);
            return;
        }

        if (punctuation.Length > 1 || punctuation == "…")
        {
            _deferredCut = end;
            return;
        }

        // A single full stop: look at the word in front of it.
        var wordStart = i;
        while (wordStart >= _lineStart && !char.IsWhiteSpace(_text[wordStart]))
        {
            wordStart--;
        }

        var word = _text.ToString(wordStart + 1, i - wordStart).TrimStart('(', '"', '\'', '“', '‘', '*', '_');
        var atLineStart = _text.ToString(_lineStart, wordStart + 1 - _lineStart).Trim().Length == 0;

        if (word.Length == 0)
        {
            Cut(end);
        }
        else if (word.Equals("etc", StringComparison.OrdinalIgnoreCase))
        {
            _deferredCut = end;
        }
        else if (Abbreviations.Contains(word) || DottedAbbreviation().IsMatch(word))
        {
            // "Dr. Smith", "e.g. this", "U.S. law", "5 p.m. today".
        }
        else if (word.Length == 1 && char.IsUpper(word[0]))
        {
            // An initial: "J. Smith".
        }
        else if (atLineStart && word.All(char.IsDigit))
        {
            // A numbered list item: "1. Open Settings".
        }
        else
        {
            Cut(end);
        }
    }

    private void NewLine()
    {
        _text.Clear();
        _lineStart = 0;
        _backticks = 0;
        _lineMayBeFence = true;
    }

    /// <summary>Emits _text[0..index) as a sentence (if it has any text) and keeps the rest.</summary>
    private void Cut(int index)
    {
        index = Math.Clamp(index, 0, _text.Length);
        var sentence = _text.ToString(0, index).Trim();
        if (sentence.Length > 0)
        {
            _out.Add(sentence);
        }

        _text.Remove(0, index);
        _lineStart = Math.Max(0, _lineStart - index);
        if (_deferredCut >= 0)
        {
            _deferredCut = _deferredCut >= index ? _deferredCut - index : -1;
        }
    }

    // Short letter groups joined by dots; not a domain ("claude.ai") or a link.
    [GeneratedRegex(@"^(?:\p{L}{1,2}\.)+\p{L}{1,2}$")]
    private static partial Regex DottedAbbreviation();
}
