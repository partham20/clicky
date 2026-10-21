using System.Globalization;
using System.Text.RegularExpressions;
using Clicky.Core.Assistant;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Dictation;

/// <summary>When the cleanup pass runs a model. Written to settings.json; do not rename.</summary>
public enum DictationCleanup
{
    /// <summary>Only on the API backend, where the fast model answers in well under a second.</summary>
    Auto,

    /// <summary>On either backend; on the subscription (Claude Code) each piece takes seconds.</summary>
    Always,

    /// <summary>Rules only: the speech engine's punctuation, the dictionary and the dash rule.</summary>
    Off,
}

/// <summary>
/// The cleanup pass on the fast model: fixes punctuation, capitals and small grammar slips in a
/// dictated piece, and never adds, drops or rewrites words. Its answer is checked against that
/// promise (word by word, ignoring fillers like "um") and thrown away if it broke it, if it is
/// slow, or if the model fails: the piece then goes in as transcribed.
/// </summary>
public sealed partial class ClaudeDictationPolisher(ClaudeAssistant assistant, ISettingsStore settings, ILogger<ClaudeDictationPolisher> logger) : IDictationPolisher
{
    /// <summary>A piece waiting longer than this goes in uncleaned.</summary>
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(4);

    public static readonly TimeSpan SubscriptionTimeout = TimeSpan.FromSeconds(25);

    internal const string SystemPrompt =
        """
        You clean up dictated text. The user spoke it and a speech recogniser wrote it down.
        Fix punctuation, capitalisation, spacing and obvious small grammar slips (agreement, a/an).
        You may drop filler words (um, uh, er) and an immediately repeated word.
        Never add words, remove other words, reorder, rephrase, summarise, translate or answer the text.
        Keep line breaks exactly where they are. Keep names, numbers and spellings as given.
        Never use em dashes or en dashes; use commas or full stops.
        Reply with the cleaned text only: no quotes, no preface, no explanation.
        """;

    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal) { "um", "uh", "er", "erm", "uhm", "hmm", "mm" };

    public async Task<string> PolishAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cleanup = settings.Current.Dictation.Cleanup;
        var backend = settings.Current.Claude.Backend;
        if (cleanup == DictationCleanup.Off || (cleanup == DictationCleanup.Auto && backend != ClaudeBackendKind.Api) || CountWords(text) < 3)
        {
            return text;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(backend == ClaudeBackendKind.Api ? ApiTimeout : SubscriptionTimeout);
        try
        {
            var started = TimeProvider.System.GetTimestamp();
            var polished = (await assistant.CompleteAsync(SystemPrompt, text, timeout.Token).ConfigureAwait(false)).Trim();
            polished = DictationText.RemoveDashes(StripQuotes(polished));
            if (!KeepsWords(text, polished))
            {
                logger.LogInformation("Dictation cleanup changed the words, so it was not used ({Ms:0} ms)", TimeProvider.System.GetElapsedTime(started).TotalMilliseconds);
                logger.LogDebug("Dictation cleanup: \"{Text}\" -> \"{Polished}\"", text, polished);
                return text;
            }

            logger.LogDebug("Dictation cleanup in {Ms:0} ms", TimeProvider.System.GetElapsedTime(started).TotalMilliseconds);
            return polished;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Dictation cleanup took too long; inserting the text as transcribed");
            return text;
        }
        catch (ClaudeBackendException ex)
        {
            logger.LogInformation("Dictation cleanup failed ({Failure}: {Message}); inserting the text as transcribed", ex.Failure, ex.Message);
            return text;
        }
    }

    /// <summary>
    /// True when <paramref name="polished"/> has the same words as <paramref name="original"/> apart
    /// from fillers and about one small grammar fix in ten words, and the same line breaks.
    /// </summary>
    internal static bool KeepsWords(string original, string polished)
    {
        if (polished.Length == 0 || original.Count(c => c == '\n') != polished.Count(c => c == '\n'))
        {
            return false;
        }

        var a = Words(original);
        var b = Words(polished);
        var allowed = Math.Max(1, a.Length / 10);
        return Math.Abs(a.Length - b.Length) <= allowed && Distance(a, b) <= allowed;
    }

    private static string StripQuotes(string text) =>
        text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '“' && text[^1] == '”')) ? text[1..^1].Trim() : text;

    private static int CountWords(string text) => WordPattern().Count(text);

    private static string[] Words(string text) =>
        [.. WordPattern().Matches(text).Select(m => m.Value.ToLower(CultureInfo.InvariantCulture)).Where(w => !Fillers.Contains(w))];

    private static int Distance(string[] a, string[] b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}
