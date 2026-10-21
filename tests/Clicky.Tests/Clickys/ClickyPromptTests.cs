using Clicky.Core.Clickys;

namespace Clicky.Tests.Clickys;

public sealed class ClickyPromptTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private static ClickyAgent Clicky(bool usesComputer = false, string instructions = "", string notes = "") =>
        new("aaaa", "Scout", instructions, notes, ClickyDraft.DefaultColor, usesComputer, 30, 0, Start, Start, Start);

    private static ClickyMessage Message(long id, ClickyMessageRole role, string text) =>
        new(id, "aaaa", null, role, text, Start.AddMinutes(id));

    [Fact]
    public void History_pairs_each_question_with_the_replies_after_it()
    {
        var turns = ClickyPrompt.History(
        [
            Message(1, ClickyMessageRole.Assistant, "orphan reply before any question"),
            Message(2, ClickyMessageRole.User, "What's the weather?"),
            Message(3, ClickyMessageRole.Assistant, "Sunny."),
            Message(4, ClickyMessageRole.System, "Couldn't finish: offline"),
            Message(5, ClickyMessageRole.Assistant, "Also warm."),
            Message(6, ClickyMessageRole.Routine, "Morning digest"),
            Message(7, ClickyMessageRole.Assistant, "Three new emails."),
            Message(8, ClickyMessageRole.User, "Unanswered question"),
            Message(9, ClickyMessageRole.System, "Stopped."),
            Message(10, ClickyMessageRole.User, "Last one"),
            Message(11, ClickyMessageRole.Assistant, "Answer."),
        ]);

        Assert.Equal(
            [("What's the weather?", "Sunny.\n\nAlso warm."), ("(Routine) Morning digest", "Three new emails."), ("Last one", "Answer.")],
            turns.Select(t => (t.UserText, t.AssistantText)));
        Assert.Equal(Start.AddMinutes(5), turns[0].At);
        Assert.All(turns, t => Assert.Null(t.Screenshot));
        Assert.All(turns, t => Assert.Empty(t.ToolCalls));
        Assert.DoesNotContain(turns, t => t.AssistantText.Contains("Couldn't finish", StringComparison.Ordinal));
    }

    [Fact]
    public void History_keeps_the_newest_exchanges()
    {
        var messages = new List<ClickyMessage>();
        for (var i = 0; i < ClickyPrompt.HistoryTurns + 5; i++)
        {
            messages.Add(Message(messages.Count + 1, ClickyMessageRole.User, $"q{i}"));
            messages.Add(Message(messages.Count + 1, ClickyMessageRole.Assistant, $"a{i}"));
        }

        var turns = ClickyPrompt.History(messages);

        Assert.Equal(ClickyPrompt.HistoryTurns, turns.Count);
        Assert.Equal("q5", turns[0].UserText);
        Assert.Equal($"q{ClickyPrompt.HistoryTurns + 4}", turns[^1].UserText);
    }

    [Fact]
    public void History_of_nothing_is_empty()
    {
        Assert.Empty(ClickyPrompt.History([]));
        Assert.Empty(ClickyPrompt.History([Message(1, ClickyMessageRole.User, "no reply yet")]));
    }

    [Fact]
    public void Chat_requests_pass_through()
    {
        Assert.Equal("Just this", ClickyPrompt.Request(new ClickyRunRequest("Just this", ClickyRunTrigger.Chat)));
        Assert.Equal("Manual", ClickyPrompt.Request(new ClickyRunRequest("Manual", ClickyRunTrigger.Manual, RoutineName: "ignored")));
    }

    [Fact]
    public void Routine_requests_are_framed_with_name_schedule_and_catch_up()
    {
        var due = new DateTimeOffset(2026, 10, 20, 7, 30, 0, TimeSpan.Zero);

        var text = ClickyPrompt.Request(new ClickyRunRequest("Summarise my inbox", ClickyRunTrigger.Routine, "r1", "  Morning digest ", due, IsCatchUp: true));

        Assert.StartsWith("(Routine \"Morning digest\", scheduled for ", text, StringComparison.Ordinal);
        Assert.Contains(due.ToLocalTime().ToString("ddd d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        Assert.Contains("running late because the PC was off or asleep", text, StringComparison.Ordinal);
        Assert.Contains("The user isn't watching", text, StringComparison.Ordinal);
        Assert.EndsWith("\n\nSummarise my inbox", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_routine_request_has_no_name_or_lateness()
    {
        var text = ClickyPrompt.Request(new ClickyRunRequest("Tidy downloads"));

        Assert.StartsWith("(Routine. The user isn't watching", text, StringComparison.Ordinal);
        Assert.DoesNotContain("running late", text, StringComparison.Ordinal);
        Assert.DoesNotContain("scheduled for", text, StringComparison.Ordinal);
        Assert.EndsWith("Tidy downloads", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_without_the_computer_says_there_is_no_screen()
    {
        var text = ClickyPrompt.Context(Clicky(), "", null, Start);

        Assert.StartsWith("# You are a Clicky", text, StringComparison.Ordinal);
        Assert.Contains("\"Scout\"", text, StringComparison.Ordinal);
        Assert.Contains("can't see or control the screen", text, StringComparison.Ordinal);
        Assert.Contains("there is no screenshot", text, StringComparison.Ordinal);
        Assert.DoesNotContain("You may see the screen", text, StringComparison.Ordinal);
        Assert.DoesNotContain("# Your instructions", text, StringComparison.Ordinal);
        Assert.DoesNotContain("# Your notes", text, StringComparison.Ordinal);
        Assert.DoesNotContain("# Your files", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_says_computer_use_is_unavailable_when_allowed_but_not_possible()
    {
        var text = ClickyPrompt.Context(Clicky(usesComputer: true), "", null, Start, canUseComputer: false);

        Assert.Contains("can't see or control the screen", text, StringComparison.Ordinal);
        Assert.Contains("Computer use isn't available in this version of Clicky yet", text, StringComparison.Ordinal);
        Assert.DoesNotContain("You may see the screen", text, StringComparison.Ordinal);
        Assert.DoesNotContain("allow it in this Clicky's settings", text, StringComparison.Ordinal);

        Assert.Contains("You may see the screen", ClickyPrompt.Context(Clicky(usesComputer: false), "", null, Start, canUseComputer: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Context_with_the_computer_includes_instructions_notes_files_and_memory()
    {
        var text = ClickyPrompt.Context(Clicky(usesComputer: true, "Check the build.", "CI is on Jenkins."), "- todo.txt (5 B)", "  # What you remember\n\nUser likes tea.  ", Start);

        Assert.Contains("You may see the screen and use the computer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("can't see or control the screen", text, StringComparison.Ordinal);
        Assert.Contains("# Your instructions\n\nCheck the build.", text, StringComparison.Ordinal);
        Assert.Contains("# Your notes", text, StringComparison.Ordinal);
        Assert.Contains("CI is on Jenkins.", text, StringComparison.Ordinal);
        Assert.Contains("# Your files", text, StringComparison.Ordinal);
        Assert.Contains("- todo.txt (5 B)", text, StringComparison.Ordinal);
        Assert.EndsWith("# What you remember\n\nUser likes tea.", text, StringComparison.Ordinal);
    }
}
