using Clicky.Core.Assistant;
using Clicky.Core.Settings;
using Clicky.Core.Suggestions;
using Clicky.Core.Voice;
using Clicky.Tests.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Clicky.Tests.Suggestions;

public sealed class SuggestionEngineTests : IDisposable
{
    private static readonly SuggestionPresence AtDesk = new(TimeSpan.FromSeconds(3), QuietReasons.None, Busy: false);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clicky-suggestions-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 4, 30, 0, TimeSpan.Zero));
    private readonly ScriptedModel _model = new();
    private readonly FakeConnectorTools _tools = new(FakeConnectorTools.Tool("gmail-work__search", readOnly: true));
    private readonly SuggestionStore _store;
    private readonly SuggestionEngine _engine;

    public SuggestionEngineTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _store = new SuggestionStore(Path.Combine(_dir, "suggestions.json"), _time, NullLogger<SuggestionStore>.Instance);
        _engine = NewEngine(_store);
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_morning_researches_early_then_shows_the_card_once_the_user_is_there()
    {
        FindingsThenIdeas(("Reply to Priya about the PCB review", "priya pcb review", "msg-1"), ("Prepare for the thermal sync", "thermal sync", "evt-1"));

        Assert.False(await _engine.TickAsync(() => AtDesk, CancellationToken.None));
        Assert.Empty(_model.Requests);

        _time.SetUtcNow(At(5, 5));
        Assert.False(await _engine.TickAsync(() => AtDesk, CancellationToken.None));
        Assert.Equal(2, _engine.Open(_engine.Today).Count);
        Assert.Equal([ModelTier.Fast, ModelTier.Fast, ModelTier.Deep], _model.Requests.Select(r => r.Tier));

        _time.SetUtcNow(At(6, 1));
        Assert.False(await _engine.TickAsync(() => AtDesk with { Quiet = QuietReasons.InCall }, CancellationToken.None));
        Assert.True(await _engine.TickAsync(() => AtDesk, CancellationToken.None));
        _engine.MarkShown(_engine.Today);
        Assert.False(await _engine.TickAsync(() => AtDesk, CancellationToken.None));

        // Research ran once.
        Assert.Equal(3, _model.Requests.Count);
    }

    [Fact]
    public async Task Decisions_are_remembered_and_count_as_engaging()
    {
        FindingsThenIdeas(("Reply to Priya", "priya reply", "msg-1"), ("Prepare for the thermal sync", "thermal sync", "evt-1"));
        _time.SetUtcNow(At(6, 30));
        Assert.True(await _engine.TickAsync(() => AtDesk, CancellationToken.None));
        _engine.MarkShown(_engine.Today);
        var ideas = _engine.Open(_engine.Today);

        _engine.Decide(ideas[0].Id, SuggestionDecision.Approved);
        _engine.MarkIgnored([ideas[1].Id]);

        // A new store reads it all back from disk.
        var reloaded = new SuggestionStore(Path.Combine(_dir, "suggestions.json"), _time, NullLogger<SuggestionStore>.Instance);
        Assert.Equal([SuggestionDecision.Approved, SuggestionDecision.Ignored], reloaded.ForDay(_engine.Today).Select(s => s.Decision));
        Assert.True(reloaded.Morning(_engine.Today)!.Engaged);
        Assert.NotNull(reloaded.Morning(_engine.Today)!.ShownAt);

        // Ignored ones stay available from the tray.
        Assert.Equal([ideas[1].Id], _engine.Open(_engine.Today).Select(s => s.Id));
    }

    [Fact]
    public async Task Adjust_rewrites_the_idea_in_place()
    {
        FindingsThenIdeas(("Reply to Priya about the PCB review", "priya pcb review", "msg-1"));
        _time.SetUtcNow(At(6, 30));
        await _engine.TickAsync(() => AtDesk, CancellationToken.None);
        var idea = _engine.Open(_engine.Today).Single();
        _model.Deep("""{"title": "Thank Priya for the review", "task": "Draft a thank-you to Priya"}""");

        var revised = await _engine.AdjustAsync(idea.Id, "just thank her", CancellationToken.None);

        Assert.Equal("Thank Priya for the review", revised!.Title);
        Assert.Equal(SuggestionDecision.Pending, revised.Decision);
        Assert.Equal("Thank Priya for the review", _store.ForDay(_engine.Today).Single().Title);
        Assert.True(_store.Morning(_engine.Today)!.Engaged);
    }

    [Fact]
    public async Task A_failed_research_is_retried_later_and_given_up_after_three_tries()
    {
        _model.Failure = new ClaudeBackendException(ClaudeFailure.Network, "offline");
        _time.SetUtcNow(At(5, 0));
        await _engine.TickAsync(() => AtDesk, CancellationToken.None);
        await _engine.TickAsync(() => AtDesk, CancellationToken.None);
        Assert.Single(_model.Requests);

        _time.Advance(SuggestionEngine.RetryAfter);
        await _engine.TickAsync(() => AtDesk, CancellationToken.None);
        _time.Advance(SuggestionEngine.RetryAfter);
        await _engine.TickAsync(() => AtDesk, CancellationToken.None);
        Assert.Equal(3, _model.Requests.Count);
        Assert.True(_store.Morning(_engine.Today)!.Researched);

        _time.Advance(SuggestionEngine.RetryAfter);
        await _engine.TickAsync(() => AtDesk, CancellationToken.None);
        Assert.Equal(3, _model.Requests.Count);
    }

    [Fact]
    public async Task Not_set_up_gives_up_for_the_day_and_find_now_says_why()
    {
        _model.Failure = new ClaudeBackendException(ClaudeFailure.NotConfigured, "Clicky needs an Anthropic API key.");
        var found = await _engine.FindNowAsync(CancellationToken.None);
        Assert.Empty(found.Ideas);
        Assert.Equal("Clicky needs an Anthropic API key.", found.Problem);
        Assert.True(_store.Morning(_engine.Today)!.Researched);
    }

    [Fact]
    public async Task Find_now_researches_any_time_of_day_once()
    {
        _time.SetUtcNow(At(15, 0));
        FindingsThenIdeas(("Reply to Priya", "priya reply", "msg-1"));
        Assert.Single((await _engine.FindNowAsync(CancellationToken.None)).Ideas);
        Assert.Single((await _engine.FindNowAsync(CancellationToken.None)).Ideas);
        Assert.Equal(3, _model.Requests.Count);
    }

    /// <summary>
    /// The module's acceptance check: no repeated ideas across three days. The editor keeps
    /// proposing yesterday's ideas (reworded, or the same items) alongside new ones; none of them
    /// may reach the card again.
    /// </summary>
    [Fact]
    public async Task No_idea_is_repeated_across_three_days()
    {
        (string Title, string Topic, string Reference)[][] proposals =
        [
            [("Reply to Priya about the PCB rev C review", "priya pcb review", "msg-1"), ("Prepare for the 10:00 thermal test sync", "thermal test sync", "evt-1"), ("Read the TPS546 errata", "tps546 errata", "https://ti.com/tps546/errata")],
            [("Answer Priya's PCB review question", "pcb review priya", "msg-1b"), ("Look at the new TPS546 errata", "tps546 controller errata", "https://ti.com/tps546/errata/"), ("Fix the flaky bootloader CI job", "bootloader ci", "run-77"), ("Book the EMC chamber for next week", "emc chamber booking", "evt-8")],
            [("Fix the failing bootloader CI job", "bootloader ci flaky", "run-78"), ("Chase the vendor about the PCB rev C quote", "vendor quote", "msg-1"), ("Draft the weekly status for Arun", "weekly status arun", "doc-3"), ("Book the EMC chamber", "emc chamber", "evt-9")],
        ];

        var shownTitles = new List<string>();
        for (var day = 0; day < 3; day++)
        {
            FindingsThenIdeas(proposals[day]);
            _time.SetUtcNow(At(6, 30).AddDays(day));
            Assert.True(await _engine.TickAsync(() => AtDesk, CancellationToken.None), $"day {day + 1}");
            var ideas = _engine.Open(_engine.Today);
            _engine.MarkShown(_engine.Today);
            foreach (var idea in ideas)
            {
                _engine.Decide(idea.Id, SuggestionDecision.Passed);
            }

            shownTitles.AddRange(ideas.Select(i => i.Title));
        }

        Assert.Equal(
            [
                "Reply to Priya about the PCB rev C review", "Prepare for the 10:00 thermal test sync", "Read the TPS546 errata",
                "Fix the flaky bootloader CI job", "Book the EMC chamber for next week",
                "Draft the weekly status for Arun",
            ],
            shownTitles);
        var all = _store.Suggestions;
        for (var i = 0; i < all.Count; i++)
        {
            Assert.False(RepeatFilter.IsRepeat(all[i], all.Take(i)), all[i].Title);
        }
    }

    [Fact]
    public void A_corrupt_history_file_is_moved_aside_and_a_new_one_started()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "suggestions.json");
        File.WriteAllText(file, "{ not json");
        var store = new SuggestionStore(file, _time, NullLogger<SuggestionStore>.Instance);

        Assert.Empty(store.Suggestions);
        store.SetMorning(new MorningRecord(new DateOnly(2026, 10, 21), Researched: true));
        Assert.Single(Directory.GetFiles(_dir, "suggestions.json.*.bad"));
        Assert.True(new SuggestionStore(file, _time, NullLogger<SuggestionStore>.Instance).Morning(new DateOnly(2026, 10, 21))!.Researched);
    }

    [Fact]
    public void Old_entries_are_pruned()
    {
        var file = Path.Combine(_dir, "suggestions.json");
        var store = new SuggestionStore(file, _time, NullLogger<SuggestionStore>.Instance);
        var today = new DateOnly(2026, 10, 21);
        store.Add([SuggestionEditorTests.Idea("Old", "old", "a", today.AddDays(-SuggestionStore.KeepDays - 1)), SuggestionEditorTests.Idea("New", "new", "b", today)]);

        Assert.Equal(["New"], new SuggestionStore(file, _time, NullLogger<SuggestionStore>.Instance).Suggestions.Select(s => s.Title));
    }

    private void FindingsThenIdeas(params (string Title, string Topic, string Reference)[] ideas)
    {
        _model.Fast("""<call tool="gmail-work__search">{"query": "newer_than:3d"}</call>""");
        _model.Fast("<findings>\n- [Gmail (Work) | msg-1] something\n</findings>");
        var json = string.Join(",", ideas.Select(i => $$"""{"title": "{{i.Title}}", "detail": "d", "why": "w", "task": "Do {{i.Title}}", "topic": "{{i.Topic}}", "sources": [{"app": "Gmail (Work)", "reference": "{{i.Reference}}"}]}"""));
        _model.Deep($$"""{"ideas": [{{json}}]}""");
    }

    private SuggestionEngine NewEngine(SuggestionStore store)
    {
        var settings = new FakeSettingsStore(new ClickySettings { Suggestions = new SuggestionSettings(MaxIdeas: 5) });
        return new SuggestionEngine(
            store,
            new SuggestionResearcher(_model, _tools, settings, _time, NullLogger<SuggestionResearcher>.Instance),
            new SuggestionEditor(_model, _time, NullLogger<SuggestionEditor>.Instance),
            new FakeMemoryService("Partha: firmware engineer"),
            settings,
            _time,
            NullLogger<SuggestionEngine>.Instance);
    }

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 21, hour, minute, 0, TimeSpan.Zero);
}
