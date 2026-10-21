namespace Clicky.Core.Memory;

/// <summary>
/// Size caps. Memory rides along on every voice and agent request, so it has to stay small; the caps
/// are enforced by refusing edits that would cross them, never by cutting text off.
/// </summary>
public sealed record MemoryLimits
{
    public static MemoryLimits Default { get; } = new();

    /// <summary>PROFILE.md body, in characters (about 1,500 tokens).</summary>
    public int ProfileChars { get; init; } = 6000;

    /// <summary>VOLATILE.md body, in characters (about 750 tokens).</summary>
    public int VolatileChars { get; init; } = 3000;

    /// <summary>One remembered fact. Longer proposals are refused rather than shortened.</summary>
    public int EntryChars { get; init; } = 280;

    /// <summary>Most edits one updater pass may apply; the rest are refused.</summary>
    public int EditsPerPass { get; init; } = 12;

    /// <summary>Budget for the prompt block. Hand edits can push a file past its cap; whole sections then drop out.</summary>
    public int PromptChars { get; init; } = 9500;

    /// <summary>VOLATILE.md older than this is labelled possibly out of date in the prompt.</summary>
    public TimeSpan VolatileStaleAfter { get; init; } = TimeSpan.FromDays(14);

    public int CapFor(MemoryFile file) => file == MemoryFile.Profile ? ProfileChars : VolatileChars;
}
