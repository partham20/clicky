namespace Clicky.Core.Memory;

/// <summary>What a new (or forgotten) memory file starts as.</summary>
public static class MemoryTemplates
{
    public const string ProfileFileName = "PROFILE.md";

    public const string VolatileFileName = "VOLATILE.md";

    public const string ProfilePreamble =
        """
        # Profile
        <!-- What Clicky remembers about you: who you are, your habits, and how you like answers.
             Clicky reads this before every answer and updates it after conversations.
             Edit or delete anything; keep one fact per "- " line under a "## " heading. -->
        """;

    public const string VolatilePreamble =
        """
        # Current work
        <!-- What you're working on right now. Clicky keeps this up to date as your work moves on.
             Edit or delete anything; keep one fact per "- " line under a "## " heading. -->
        """;

    /// <summary>Sections the updater files new facts under. People can add their own.</summary>
    public static IReadOnlyList<string> ProfileSections { get; } = ["About me", "How to answer", "Habits and tools"];

    public static IReadOnlyList<string> VolatileSections { get; } = ["Current project", "Open threads"];

    public static string FileName(MemoryFile file) => file == MemoryFile.Profile ? ProfileFileName : VolatileFileName;

    public static MemoryDocument Empty(MemoryFile file) => new(
        file == MemoryFile.Profile ? ProfilePreamble : VolatilePreamble,
        [.. (file == MemoryFile.Profile ? ProfileSections : VolatileSections).Select(h => new MemorySection(h, []))]);
}
