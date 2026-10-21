using System.Text.RegularExpressions;
using Clicky.Core.Assistant;
using Clicky.Core.Memory;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Dictation;

/// <summary>
/// Screen-aware drafting: a dictation that starts "type a reply to this" (or "draft a response",
/// "write an answer…") is not typed as said. Claude looks at the screen and writes the reply in
/// the user's own voice, using what memory knows about them, and that draft is inserted instead.
/// </summary>
/// <remarks>
/// Each draft is its own one-turn conversation, so Talk's running conversation is not touched,
/// and the exchange is recorded in memory as a dictation turn.
/// </remarks>
public sealed partial class ScreenDrafter(ClaudeAssistant assistant, IMemoryService memory, ILogger<ScreenDrafter> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// The request in a dictation such as "Type a reply to this saying I'll be there at five." —
    /// the whole sentence, so Claude also sees the "saying…" part — or null if it is ordinary dictation.
    /// </summary>
    public static string? FindRequest(string dictated)
    {
        ArgumentNullException.ThrowIfNull(dictated);
        var text = dictated.Trim();
        return Command().IsMatch(text) ? text : null;
    }

    /// <summary>Asks Claude for the draft. Returns the text to insert (plain, no dashes).</summary>
    /// <exception cref="ClaudeBackendException">The backend is not set up or failed.</exception>
    public async Task<string> DraftAsync(string request, RequestScreenshot? screenshot, string app, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        var remembered = await memory.GetPromptAsync(cancellationToken).ConfigureAwait(false);
        var conversation = new Conversation(_time, string.IsNullOrWhiteSpace(remembered) ? null : remembered);
        var question = BuildQuestion(request, app, screenshot is not null);
        var started = _time.GetTimestamp();

        string? draft = null;
        await foreach (var reply in assistant.ReplyAsync(conversation, new ClaudeRequest(question, RequestSource.Typed, screenshot), cancellationToken).ConfigureAwait(false))
        {
            if (reply is ReplyCompleted completed)
            {
                draft = completed.SpokenText;
                logger.LogInformation(
                    "Dictation draft: {Chars} chars from {Model} in {Ms:0} ms ({Stop})",
                    draft.Length,
                    completed.Model,
                    _time.GetElapsedTime(started).TotalMilliseconds,
                    completed.Stop);
            }
        }

        draft = Clean(draft ?? string.Empty);
        if (draft.Length > 0)
        {
            memory.RecordTurn(new MemoryTurn(request, draft, _time.GetUtcNow(), MemoryTurnMode.Dictation));
        }

        return draft;
    }

    internal static string BuildQuestion(string request, string app, bool hasScreenshot) =>
        $"""
        [Dictation: screen-aware drafting in {app}]
        The user dictated this request: "{request}"
        Write the text they want typed at their cursor, as if they wrote it themselves: their voice, tone and usual length, using what you remember about them.
        {(hasScreenshot ? "The screenshot shows what they are replying to; base the reply on it." : "There is no screenshot; work from the request alone.")}
        Answer with only that text, through say: no preface, no quotation marks, no markdown, no em dashes, no signature unless they always sign that way. Do not point, highlight or draw.
        """;

    /// <summary>Plain text: no surrounding quotes, no markdown emphasis, no dashes.</summary>
    internal static string Clean(string draft)
    {
        var text = draft.Trim();
        if (text.Length >= 2 && (text[0], text[^1]) is ('"', '"') or ('“', '”'))
        {
            text = text[1..^1].Trim();
        }

        text = Emphasis().Replace(text, "${t}");
        return DictationText.RemoveDashes(text);
    }

    // "type a reply to this", "draft a response", "write an answer to this email", "reply to this".
    [GeneratedRegex(@"^(?:(?:please|can you|could you|okay|ok)[,\s]+)?(?:(?:type|draft|write)\s+(?:me\s+)?(?:a|an|the)?\s*(?:quick\s+|short\s+)?(?:reply|response|answer)|reply\s+to\s+(?:this|that|him|her|them))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Command();

    [GeneratedRegex(@"\*\*(?<t>.+?)\*\*|__(?<t>.+?)__", RegexOptions.CultureInvariant)]
    private static partial Regex Emphasis();
}
