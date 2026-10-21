using System.Text.RegularExpressions;

namespace Clicky.Core.Memory;

/// <summary>
/// Spots turns that should reach memory straight away instead of waiting for the conversation to go
/// quiet: feedback on how Clicky behaves, stated preferences, and "remember"/"forget" requests. A
/// false positive costs one fast-model call that usually proposes nothing; a miss is still caught by
/// the end-of-conversation pass.
/// </summary>
public static partial class FeedbackCue
{
    public static bool IsFeedback(string? userText) => !string.IsNullOrWhiteSpace(userText) && Pattern().IsMatch(userText);

    [GeneratedRegex(
        @"\b(" +
        @"you (talk|speak|say|ramble|go on)( way| far)? too (much|long|fast|slow|loud|quiet)" +
        @"|(way |far )?too (long|wordy|verbose|chatty)\b" +
        @"|(be|keep it|keep (your )?(answers|replies)) (more )?(brief|briefer|concise|short|shorter|quick|quicker)" +
        @"|(shorter|briefer|less wordy|more detail|more detailed) (answers|replies|please)" +
        @"|(stop|quit) (saying|doing|calling|telling|asking|pointing|talking|reading)" +
        @"|(don'?t|do not|never) (ever )?(say|do|call|tell|ask|read|point|use|talk|speak|explain|repeat|start|open)" +
        @"|always (say|do|call|use|answer|reply|speak|explain|show|point|ask)" +
        @"|from now on|in (the )?future|next time" +
        @"|remember (that|this|to|my|i)|don'?t forget|forget (that|this|about|what|my)" +
        @"|i (prefer|like it when|hate it when|don'?t like it when|want you to)" +
        @"|call me|my name is|i'?m called" +
        @")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
