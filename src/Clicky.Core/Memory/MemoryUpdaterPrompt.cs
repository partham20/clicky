using System.Globalization;
using System.Text;

namespace Clicky.Core.Memory;

/// <summary>Builds the request for the updater pass that runs after a conversation.</summary>
/// <remarks>
/// Kept here rather than in Clicky.Core/Prompts because the memory rules (caps, file roles, edit format)
/// live in this folder and must change together with <see cref="MemoryMerger"/>.
/// </remarks>
public static class MemoryUpdaterPrompt
{
    /// <summary>Per-turn clip for the transcript sent to the updater. Only the request is clipped, never memory.</summary>
    public const int TurnChars = 1500;

    public const string System =
        """
        You maintain the long-term memory of Clicky, a voice assistant that lives next to the user's mouse pointer on Windows. After each conversation you read the transcript and propose small edits to two Markdown files:

        - PROFILE: durable facts about the user. Who they are and what they do ("About me"), how they want Clicky to behave ("How to answer"), and lasting habits, apps and tools ("Habits and tools").
        - VOLATILE: what they are working on now ("Current project") and loose ends Clicky should follow up on ("Open threads"). Expect this to change often; remove entries that are finished or replaced.

        What to remember:
        - Anything the user explicitly asks you to remember, and any preference they state ("I prefer…", "always…", "never…", "call me…").
        - Feedback about Clicky's behaviour, even when it is casual or annoyed. "You talk too much" becomes "Keep spoken answers to one or two sentences unless asked for more." "Stop saying 'great question'" becomes "Don't open answers with praise." Feedback always goes in PROFILE under "How to answer".
        - Facts that will clearly matter again: their role, the tools they use daily, the project they are in the middle of.
        - When the user asks you to forget something, remove it.

        What never to remember:
        - Passwords, keys, tokens, account numbers, or anything copied from a login or payment screen.
        - Health, money, religion, politics or relationships, unless the user explicitly asked you to remember that specific thing.
        - Private details about other people beyond their name and role.
        - One-off questions, small talk, and things visible on screen that won't matter tomorrow.
        - Guesses. Only record what the user said or clearly showed. Don't record what Clicky said unless the user agreed with it.

        How to edit:
        - Each entry is one short line in plain words, at most 200 characters, written as a fact or an instruction to Clicky. No dates unless the date matters.
        - Prefer "replace" over "add" when an entry already covers the topic; a newer preference replaces the older one rather than sitting beside it.
        - "old" must be copied exactly from an existing entry, without its "- ".
        - Stay within the room left in each file. If a file is nearly full, merge or remove stale entries to make room.
        - Most conversations need no edits. Then return an empty list.

        Reply with only this JSON and nothing else:
        {"edits":[{"op":"add","file":"profile","section":"How to answer","text":"…"},{"op":"replace","file":"volatile","section":"Current project","old":"…","text":"…"},{"op":"remove","file":"volatile","section":"Open threads","old":"…"}]}
        "op" is add, replace or remove. "file" is profile or volatile.
        """;

    public static string User(MemorySet memory, IReadOnlyList<MemoryTurn> turns, MemoryLimits limits, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(limits);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Today is {now:dddd d MMMM yyyy}.\n\n");
        AppendFile(sb, "PROFILE", memory.Profile, limits.ProfileChars);
        AppendFile(sb, "VOLATILE", memory.Volatile, limits.VolatileChars);

        sb.Append("<conversation>\n");
        foreach (var turn in turns)
        {
            sb.Append(CultureInfo.InvariantCulture, $"[{turn.At.ToLocalTime():HH:mm}, {turn.Mode.ToString().ToLowerInvariant()}]\n");
            sb.Append("User: ").Append(Clip(turn.User)).Append('\n');
            if (!string.IsNullOrWhiteSpace(turn.Assistant))
            {
                sb.Append("Clicky: ").Append(Clip(turn.Assistant)).Append('\n');
            }
        }

        sb.Append("</conversation>\n\nReply with the JSON edits.");
        return sb.ToString();
    }

    private static void AppendFile(StringBuilder sb, string name, MemoryDocument document, int cap)
    {
        var body = document.BodyMarkdown();
        var sections = string.Join(", ", document.Sections.Select(s => s.Heading));
        sb.Append(CultureInfo.InvariantCulture, $"<{name} room_left=\"{Math.Max(0, cap - body.Length)} characters\" sections=\"{sections}\">\n");
        sb.Append(body.Length == 0 ? "(empty)" : body).Append('\n');
        sb.Append(CultureInfo.InvariantCulture, $"</{name}>\n\n");
    }

    private static string Clip(string text)
    {
        var t = text.Trim();
        return t.Length <= TurnChars ? t : string.Concat(t.AsSpan(0, TurnChars), " …(cut)");
    }
}
