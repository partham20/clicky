using System.Text;

namespace Clicky.Core.Actions;

/// <summary>A piece of streamed model text: either words to speak, or the JSON inside one action tag.</summary>
public readonly record struct TagReaderToken(bool IsAction, string Text);

/// <summary>
/// Splits streamed text into speech and <c>&lt;action&gt;{…}&lt;/action&gt;</c> tags, for backends that
/// can't take tools (Claude Code headless). Text is released as soon as it can't be the start of a
/// tag, so speech isn't held back; only a trailing "&lt;", "&lt;ac", … waits for the next chunk.
/// </summary>
public sealed class ActionTagReader
{
    public const string Open = "<action>";
    public const string Close = "</action>";

    /// <summary>An unterminated tag longer than this is given up on and spoken as text.</summary>
    public const int MaxTagLength = 16_384;

    private readonly StringBuilder _pending = new();
    private bool _inTag;

    public IReadOnlyList<TagReaderToken> Push(string chunk)
    {
        _pending.Append(chunk);
        var tokens = new List<TagReaderToken>();
        Drain(tokens, final: false);
        return tokens;
    }

    /// <summary>End of the reply: releases whatever is held back (an unclosed tag becomes text).</summary>
    public IReadOnlyList<TagReaderToken> Flush()
    {
        var tokens = new List<TagReaderToken>();
        Drain(tokens, final: true);
        if (_pending.Length > 0)
        {
            tokens.Add(new TagReaderToken(false, _inTag ? Open + _pending : _pending.ToString()));
            _pending.Clear();
        }

        _inTag = false;
        return tokens;
    }

    private void Drain(List<TagReaderToken> tokens, bool final)
    {
        while (_pending.Length > 0)
        {
            var text = _pending.ToString();
            if (_inTag)
            {
                var end = text.IndexOf(Close, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    if (text.Length > MaxTagLength)
                    {
                        tokens.Add(new TagReaderToken(false, Open + text));
                        _pending.Clear();
                        _inTag = false;
                    }

                    return;
                }

                tokens.Add(new TagReaderToken(true, text[..end].Trim()));
                _pending.Remove(0, end + Close.Length);
                _inTag = false;
                continue;
            }

            var start = text.IndexOf(Open, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                if (start > 0)
                {
                    tokens.Add(new TagReaderToken(false, text[..start]));
                }

                _pending.Remove(0, start + Open.Length);
                _inTag = true;
                continue;
            }

            // Hold back a tail that could still grow into "<action>".
            var keep = final ? 0 : PartialOpenLength(text);
            if (text.Length > keep)
            {
                tokens.Add(new TagReaderToken(false, text[..^keep]));
                _pending.Remove(0, text.Length - keep);
            }

            return;
        }
    }

    private static int PartialOpenLength(string text)
    {
        for (var length = Math.Min(Open.Length - 1, text.Length); length > 0; length--)
        {
            if (Open.StartsWith(text[^length..], StringComparison.OrdinalIgnoreCase))
            {
                return length;
            }
        }

        return 0;
    }
}
