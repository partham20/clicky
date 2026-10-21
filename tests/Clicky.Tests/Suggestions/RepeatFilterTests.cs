using Clicky.Core.Suggestions;

namespace Clicky.Tests.Suggestions;

public sealed class RepeatFilterTests
{
    private static readonly DateOnly Day = new(2026, 10, 21);

    [Theory]
    [InlineData("Reply to Priya about the PCB review", "priya pcb review", "Answer Priya's PCB review question", "pcb review for priya")]
    [InlineData("Prepare for the thermal test sync", "thermal test sync", "Prep notes for today's thermal test sync", "thermal sync prep")]
    [InlineData("Read the TPS546 errata", "tps546 errata", "New errata for the TPS546", "tps546 controller errata")]
    public void Reworded_ideas_are_repeats(string title, string topic, string otherTitle, string otherTopic)
    {
        var earlier = Idea(title, topic, "a");
        var later = Idea(otherTitle, otherTopic, "b");
        Assert.True(RepeatFilter.IsRepeat(later, [earlier]));
    }

    [Theory]
    [InlineData("Reply to Priya about the PCB review", "priya pcb review", "Thank Priya for the lunch invite", "priya lunch")]
    [InlineData("Prepare for the thermal test sync", "thermal test sync", "Review the EMC test report", "emc report")]
    [InlineData("Fix the flaky bootloader CI job", "bootloader ci", "Update the bootloader README", "bootloader readme")]
    public void Different_ideas_are_not(string title, string topic, string otherTitle, string otherTopic)
    {
        var earlier = Idea(title, topic, "a");
        var later = Idea(otherTitle, otherTopic, "b");
        Assert.False(RepeatFilter.IsRepeat(later, [earlier]));
    }

    [Fact]
    public void The_same_item_is_a_repeat_however_it_is_worded()
    {
        var earlier = Idea("Answer the vendor about lead times", "vendor lead time", "https://mail.example.com/msg/42#inbox");
        var later = Idea("Chase the purchase order", "purchase order", "HTTPS://mail.example.com/msg/42/");
        Assert.True(RepeatFilter.IsRepeat(later, [earlier]));
    }

    [Fact]
    public void The_same_topic_is_a_repeat()
    {
        Assert.True(RepeatFilter.IsRepeat(Idea("One thing", "Linear Sprint Planning", "x"), [Idea("Another thing", "sprint planning linear", "y")]));
    }

    [Fact]
    public void Keep_new_drops_repeats_of_earlier_and_of_each_other_in_order()
    {
        var earlier = Idea("Reply to Priya about the PCB review", "priya pcb review", "msg-1");
        var candidates = new[]
        {
            Idea("Answer Priya's PCB review", "pcb review", "msg-7"),
            Idea("Prepare for the thermal test sync", "thermal test sync", "evt-1"),
            Idea("Prep for the thermal test sync meeting", "thermal sync", "evt-1"),
            Idea("Fix the flaky bootloader CI job", "bootloader ci", "run-5"),
        };

        var kept = RepeatFilter.KeepNew(candidates, [earlier]);

        Assert.Equal(["Prepare for the thermal test sync", "Fix the flaky bootloader CI job"], kept.Select(k => k.Title));
    }

    [Fact]
    public void Words_drop_stop_words_and_endings()
    {
        Assert.True(RepeatFilter.Words("Reply to Priya's boards testing").SetEquals(["priya", "board", "test"]));
    }

    private static Suggestion Idea(string title, string topic, string reference) =>
        new(Guid.NewGuid().ToString("N")[..12], Day, default, title, string.Empty, string.Empty, title, topic, [new SuggestionSource("Gmail", reference)]);
}
