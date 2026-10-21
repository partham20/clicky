using Clicky.Core.Suggestions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Clicky.Tests.Suggestions;

public sealed class SuggestionEditorTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 5, 10, 0, TimeSpan.Zero));
    private readonly ScriptedModel _model = new();

    public SuggestionEditorTests() => _time.SetLocalTimeZone(TimeZoneInfo.Utc);

    [Fact]
    public async Task Reads_fenced_json_and_keeps_at_most_max_ideas()
    {
        _model.Deep("""
            Here you go:
            ```json
            {"ideas": [
              {"title": "Reply to Priya about the PCB rev C review", "detail": "She asked on Tuesday.", "why": "Due today.", "task": "Draft a reply to Priya", "topic": "priya pcb review", "sources": [{"app": "Gmail (Work)", "reference": "msg-1"}]},
              {"title": "Prepare for the 10:00 thermal test sync", "detail": "d", "why": "w", "task": "Brief me", "topic": "thermal test sync", "sources": [{"app": "Google Calendar", "reference": "evt-9"}]},
              {"title": "Fix the flaky bootloader CI job", "detail": "d", "why": "w", "task": "t", "topic": "bootloader ci", "sources": ["GitHub"]},
              {"title": "", "detail": "no title, dropped"}
            ]}
            ```
            """);

        var ideas = await Editor().PickAsync("- notes", "memory", [], maxIdeas: 2, noRepeatDays: 14, CancellationToken.None);

        Assert.Equal(2, ideas.Count);
        var first = ideas[0];
        Assert.Equal("Reply to Priya about the PCB rev C review", first.Title);
        Assert.Equal(Today, first.Day);
        Assert.Equal(SuggestionDecision.Pending, first.Decision);
        Assert.Equal("From Gmail (Work)", first.SourceLine);
        Assert.Equal("msg-1", first.Sources[0].Reference);
        Assert.NotEqual(ideas[0].Id, ideas[1].Id);
        Assert.Contains("at most 2 ideas", _model.Requests[0].System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drops_ideas_that_repeat_recent_ones_and_tells_the_editor_about_the_users_taste()
    {
        var shown = Idea("Reply to Priya about the PCB review", "priya pcb review", "msg-1", Today.AddDays(-2)) with { Decision = SuggestionDecision.Approved };
        var skipped = Idea("Read the TPS546 errata", "tps546 errata", "https://ti.com/errata", Today.AddDays(-1)) with { Decision = SuggestionDecision.Passed };
        var old = Idea("Book the EMC chamber", "emc chamber booking", "evt-2", Today.AddDays(-30)) with { Decision = SuggestionDecision.Declined };
        _model.Deep("""
            {"ideas": [
              {"title": "Answer Priya's PCB review question", "topic": "pcb review priya", "task": "t", "sources": [{"app": "Gmail (Work)", "reference": "msg-1"}]},
              {"title": "Look at the new TPS546 errata", "topic": "tps546 errata", "task": "t", "sources": [{"app": "Web", "reference": "https://ti.com/errata/"}]},
              {"title": "Book the EMC chamber", "topic": "emc chamber booking", "task": "t", "sources": [{"app": "Google Calendar", "reference": "evt-2"}]}
            ]}
            """);

        var ideas = await Editor().PickAsync("- notes", string.Empty, [shown, skipped, old], maxIdeas: 3, noRepeatDays: 14, CancellationToken.None);

        // Older than the no-repeat window, so it may come back.
        Assert.Equal(["Book the EMC chamber"], ideas.Select(i => i.Title));
        var input = _model.Requests[0].User;
        Assert.Contains("Reply to Priya about the PCB review (topic: priya pcb review)", input, StringComparison.Ordinal);
        Assert.DoesNotContain("(topic: emc chamber booking)", input, StringComparison.Ordinal);
        Assert.Contains("- Did it: Reply to Priya about the PCB review", input, StringComparison.Ordinal);
        Assert.Contains("- Skipped it: Read the TPS546 errata", input, StringComparison.Ordinal);
        Assert.Contains("- Closed it: Book the EMC chamber", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unusable_reply_gives_no_ideas()
    {
        _model.Deep("Sorry, nothing today.");
        Assert.Empty(await Editor().PickAsync("- notes", string.Empty, [], 3, 14, CancellationToken.None));
    }

    [Fact]
    public async Task Revise_keeps_the_id_and_unchanged_facts_and_records_the_request()
    {
        var idea = Idea("Reply to Priya about the PCB review", "priya pcb review", "msg-1", Today);
        _model.Deep("""{"title": "Draft a short thank-you to Priya", "detail": "Two lines.", "task": "Draft a two-line thank-you to Priya, don't send it"}""");

        var revised = await Editor().ReviseAsync(idea, "make it shorter, just thank her", CancellationToken.None);

        Assert.NotNull(revised);
        Assert.Equal(idea.Id, revised.Id);
        Assert.Equal("Draft a short thank-you to Priya", revised.Title);
        Assert.Equal(idea.Why, revised.Why);
        Assert.Equal(idea.Topic, revised.Topic);
        Assert.Equal(idea.Sources, revised.Sources);
        Assert.Equal(["make it shorter, just thank her"], revised.Adjustments);
        Assert.Contains("\"reference\":\"msg-1\"", _model.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("The user asked: make it shorter, just thank her", _model.Requests[0].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removes_em_dashes_and_caps_lengths()
    {
        var longTitle = new string('a', SuggestionEditor.MaxTitleChars + 50);
        _model.Deep($$"""[{"title": "Check the build — it failed twice", "task": "t"}, {"title": "{{longTitle}}", "task": "t"}]""");

        var ideas = await Editor().PickAsync("- notes", string.Empty, [], 3, 14, CancellationToken.None);

        Assert.Equal("Check the build, it failed twice", ideas[0].Title);
        Assert.Equal(SuggestionEditor.MaxTitleChars, ideas[1].Title.Length);
        Assert.EndsWith("…", ideas[1].Title, StringComparison.Ordinal);
    }

    internal static Suggestion Idea(string title, string topic, string reference, DateOnly day) =>
        new(Guid.NewGuid().ToString("N")[..12], day, day.ToDateTime(new TimeOnly(5, 30)), title, "Some detail.", "Because.", $"Do: {title}", topic, [new SuggestionSource("Gmail (Work)", reference)]);

    private SuggestionEditor Editor() => new(_model, _time, NullLogger<SuggestionEditor>.Instance);
}
