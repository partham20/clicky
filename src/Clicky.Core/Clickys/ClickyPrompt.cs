using System.Globalization;
using System.Text;
using Clicky.Core.Assistant;

namespace Clicky.Core.Clickys;

/// <summary>
/// What a Clicky run tells Claude on top of Clicky's usual system prompt: who this Clicky is, its
/// instructions, notes and files, and what the user's memory says. It rides in the conversation's
/// memory slot, which both backends append to the system prompt and keep fixed for the run.
/// </summary>
public static class ClickyPrompt
{
    /// <summary>Earlier chat exchanges sent with a run, as history.</summary>
    public const int HistoryTurns = 20;

    /// <param name="canUseComputer">
    /// Whether this run may see and use the computer: the Clicky allows it and computer use is
    /// available. Defaults to the Clicky's setting.
    /// </param>
    public static string Context(ClickyAgent clicky, string files, string? userMemory, DateTimeOffset now, bool? canUseComputer = null)
    {
        var text = new StringBuilder();
        text.Append("# You are a Clicky\n\n")
            .Append(CultureInfo.InvariantCulture, $"You are \"{clicky.Name}\", one of the user's Clickys: a persistent helper with its own chat in Clicky Home. ")
            .Append("The user writes to you there, and routines can send you tasks on a schedule while nobody is watching. ")
            .Append(CultureInfo.InvariantCulture, $"It is {now.ToLocalTime():dddd d MMMM yyyy, HH:mm} on the user's PC.\n\n")
            .Append("This overrides the spoken style above: your replies are read in Home, not spoken. ")
            .Append("Write Markdown (headings, lists, tables and code blocks are fine) and make the reply complete enough to read later on its own. ")
            .Append("If the task produces a standalone document such as a report or a web page, put the whole document in one ```html code block so Home can preview it.\n\n");

        if (canUseComputer ?? clicky.UsesComputer)
        {
            text.Append("You may see the screen and use the computer for this task, following the rules above. ")
                .Append("The user may not be watching: when something needs their permission, say exactly what you need and stop.\n\n");
        }
        else
        {
            text.Append("You can't see or control the screen in this chat: there is no screenshot, so don't point, highlight, draw, click, type, press keys or scroll. ")
                .Append(clicky.UsesComputer
                    ? "Computer use isn't available in this version of Clicky yet; if the task needs it, say so.\n\n"
                    : "If the task needs the computer, say so; the user can allow it in this Clicky's settings.\n\n");
        }

        if (clicky.Instructions.Length > 0)
        {
            text.Append("# Your instructions\n\n").Append(clicky.Instructions).Append("\n\n");
        }

        if (clicky.Notes.Length > 0)
        {
            text.Append("# Your notes\n\nWhat you've been asked to keep in mind:\n\n").Append(clicky.Notes).Append("\n\n");
        }

        if (files.Length > 0)
        {
            text.Append("# Your files\n\nIn your files folder (text files are included):\n\n").Append(files).Append("\n\n");
        }

        if (!string.IsNullOrWhiteSpace(userMemory))
        {
            text.Append(userMemory.Trim()).Append("\n\n");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The words sent for a run: the user's message as is, or a routine's prompt with when it was due.</summary>
    public static string Request(ClickyRunRequest request)
    {
        if (request.Trigger != ClickyRunTrigger.Routine)
        {
            return request.Prompt;
        }

        var text = new StringBuilder();
        text.Append("(Routine");
        if (!string.IsNullOrWhiteSpace(request.RoutineName))
        {
            text.Append(CultureInfo.InvariantCulture, $" \"{request.RoutineName.Trim()}\"");
        }

        if (request.ScheduledFor is { } due)
        {
            text.Append(CultureInfo.InvariantCulture, $", scheduled for {due.ToLocalTime():ddd d MMM HH:mm}");
        }

        if (request.IsCatchUp)
        {
            text.Append(", running late because the PC was off or asleep");
        }

        text.Append(". The user isn't watching; your reply waits in your chat.)\n\n").Append(request.Prompt);
        return text.ToString();
    }

    /// <summary>
    /// Earlier chat as conversation turns: each user or routine message with the replies after it,
    /// newest <see cref="HistoryTurns"/> exchanges. Clicky's own system notes are left out.
    /// </summary>
    public static IReadOnlyList<ConversationTurn> History(IReadOnlyList<ClickyMessage> messages)
    {
        var turns = new List<ConversationTurn>();
        string? asked = null;
        DateTimeOffset at = default;
        var answer = new StringBuilder();

        void Flush()
        {
            if (asked is not null && answer.Length > 0)
            {
                turns.Add(new ConversationTurn(asked, null, answer.ToString().Trim(), [], at));
            }

            asked = null;
            answer.Clear();
        }

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case ClickyMessageRole.User or ClickyMessageRole.Routine:
                    Flush();
                    asked = message.Role == ClickyMessageRole.Routine ? "(Routine) " + message.Text : message.Text;
                    at = message.At;
                    break;
                case ClickyMessageRole.Assistant when asked is not null:
                    answer.Append(message.Text).Append("\n\n");
                    at = message.At;
                    break;
            }
        }

        Flush();
        return turns.Count > HistoryTurns ? turns[^HistoryTurns..] : turns;
    }
}
