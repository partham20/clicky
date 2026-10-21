namespace Clicky.Core.Documents;

/// <summary>
/// A quick, deliberately high token count for budgeting documents without a tokenizer: about four
/// characters a token for ASCII (English prose and code run 3.5 to 4.5), one a token for everything
/// else (CJK is about one, accented Latin less, so mixed text comes out over rather than under).
/// </summary>
public static class TokenEstimate
{
    public const int AsciiCharsPerToken = 4;

    public static int Of(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var ascii = 0;
        var other = 0;
        foreach (var c in text)
        {
            if (c < 0x80)
            {
                ascii++;
            }
            else if (!char.IsLowSurrogate(c))
            {
                other++;
            }
        }

        return ((ascii + AsciiCharsPerToken - 1) / AsciiCharsPerToken) + other;
    }

    /// <summary>About how many characters of plain English make <paramref name="tokens"/>.</summary>
    public static int CharsFor(int tokens) => tokens * AsciiCharsPerToken;
}
