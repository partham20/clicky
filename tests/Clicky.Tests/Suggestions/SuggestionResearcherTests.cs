using System.Text.Json;
using Clicky.Core.Assistant;
using Clicky.Core.Connectors;
using Clicky.Core.Settings;
using Clicky.Core.Suggestions;
using Clicky.Tests.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Clicky.Tests.Suggestions;

public sealed class SuggestionResearcherTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 5, 0, 0, TimeSpan.Zero));
    private readonly ScriptedModel _model = new();

    public SuggestionResearcherTests() => _time.SetLocalTimeZone(TimeZoneInfo.Utc);

    [Fact]
    public async Task Only_read_only_tools_are_offered_and_a_write_tool_is_refused_even_if_asked_for()
    {
        var tools = new FakeConnectorTools(
            FakeConnectorTools.Tool("gmail-work__search", readOnly: true),
            FakeConnectorTools.Tool("gmail-work__send", readOnly: false));
        _model
            .Fast("""
                  <call tool="gmail-work__search">{"query": "newer_than:3d"}</call>
                  <call tool="gmail-work__send">{"query": "hi"}</call>
                  """)
            .Fast("<findings>\n- [Gmail (Work) | msg-1] Priya asked for the PCB review\n</findings>");

        var outcome = await Researcher(tools).ResearchAsync(string.Empty, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Contains("gmail-work__search", _model.Requests[0].System, StringComparison.Ordinal);
        Assert.DoesNotContain("gmail-work__send", _model.Requests[0].System, StringComparison.Ordinal);
        Assert.Equal(["gmail-work__search"], tools.Calls.Select(c => c.Name));
        Assert.Contains("output of gmail-work__search", _model.Requests[1].User, StringComparison.Ordinal);
        Assert.Contains("is not one of your tools", _model.Requests[1].User, StringComparison.Ordinal);
        Assert.Equal("- [Gmail (Work) | msg-1] Priya asked for the PCB review", outcome.Findings);
        Assert.Equal(2, outcome.Rounds);
    }

    [Fact]
    public async Task Web_searches_go_to_the_model_and_are_limited()
    {
        var calls = string.Join('\n', Enumerable.Range(1, 6).Select(i => $$"""<call tool="web_search">{"query": "q{{i}}"}</call>"""));
        _model.Fast(calls).Fast("<findings>- [Web | https://example.com/2] news</findings>");

        var outcome = await Researcher(new FakeConnectorTools()).ResearchAsync(string.Empty, CancellationToken.None);

        Assert.Equal(SuggestionResearcher.MaxWebSearches, _model.Searches.Count);
        Assert.Equal(SuggestionResearcher.MaxWebSearches, outcome!.WebSearches);
        Assert.Contains("isn't available any more today", _model.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_to_research_with_returns_null_without_asking_the_model()
    {
        var settings = new FakeSettingsStore(new ClickySettings { Suggestions = new SuggestionSettings(SearchWeb: false) });
        var researcher = new SuggestionResearcher(_model, new FakeConnectorTools(FakeConnectorTools.Tool("x__send", readOnly: false)), settings, _time, NullLogger<SuggestionResearcher>.Instance);

        Assert.Null(await researcher.ResearchAsync(string.Empty, CancellationToken.None));
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task The_last_round_asks_for_findings_and_stops()
    {
        var tools = new FakeConnectorTools(FakeConnectorTools.Tool("linear__issues", readOnly: true, connector: "Linear", account: string.Empty));
        for (var i = 0; i < SuggestionResearcher.MaxRounds; i++)
        {
            _model.Fast("""<call tool="linear__issues">{"query": "assigned"}</call>""");
        }

        var outcome = await Researcher(tools).ResearchAsync(string.Empty, CancellationToken.None);

        Assert.Equal(SuggestionResearcher.MaxRounds, _model.Requests.Count);
        Assert.Contains("This is the last round", _model.Requests[^1].User, StringComparison.Ordinal);
        Assert.Equal(SuggestionResearcher.MaxRounds - 1, tools.Calls.Count);
        Assert.Equal(string.Empty, outcome!.Findings);
    }

    [Fact]
    public async Task Long_results_are_cut_and_the_memory_and_window_are_in_the_prompt()
    {
        var tools = new FakeConnectorTools(FakeConnectorTools.Tool("gmail-work__search", readOnly: true))
        {
            Reply = (_, _) => new ConnectorToolResult(false, [new ConnectorContent.Text(new string('x', SuggestionResearcher.MaxResultChars * 2))]),
        };
        _model.Fast("""<call tool="gmail-work__search">{"query": "a"}</call>""").Fast("<findings></findings>");

        var outcome = await Researcher(tools).ResearchAsync("Works on Delta PSU firmware.", CancellationToken.None);

        Assert.Equal(string.Empty, outcome!.Findings);
        Assert.Contains("[... cut,", _model.Requests[1].User, StringComparison.Ordinal);
        Assert.Contains("Works on Delta PSU firmware.", _model.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("Connected apps: Gmail (Work).", _model.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("the last 72 hours", _model.Requests[0].System, StringComparison.Ordinal);
        Assert.Contains("query (string, required): What to look for", _model.Requests[0].System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_failures_propagate()
    {
        _model.Failure = new ClaudeBackendException(ClaudeFailure.Network, "offline");
        await Assert.ThrowsAsync<ClaudeBackendException>(() => Researcher(new FakeConnectorTools()).ResearchAsync(string.Empty, CancellationToken.None));
    }

    [Fact]
    public void Protocol_reads_calls_findings_and_ignores_imagined_results()
    {
        var turn = ResearchProtocol.Parse("""
            Let me look.
            <call tool="a__b">{"q": 1}</call>
            <call tool='c__d'>not json</call>
            <result call="1">made up</result>
            <call tool="e__f">{}</call>
            """);
        Assert.Null(turn.Findings);
        Assert.Equal(["a__b", "c__d"], turn.Calls.Select(c => c.Tool));
        Assert.Equal(1, turn.Calls[0].Arguments!.Value.GetProperty("q").GetInt32());
        Assert.Null(turn.Calls[1].Arguments);

        Assert.Equal("- one", ResearchProtocol.Parse("<findings>\n- one\n</findings>").Findings);
        Assert.Equal("- cut off", ResearchProtocol.Parse("<findings>\n- cut off").Findings);
        Assert.Equal("plain notes", ResearchProtocol.Parse("plain notes").Findings);
    }

    [Fact]
    public void Inputs_summarize_a_schema()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"q":{"type":"string"},"n":{"type":["integer","null"],"description":"How many"}},"required":["q"]}""");
        Assert.Equal("q (string, required); n (integer|null): How many", SuggestionResearcher.Inputs(schema.RootElement));
    }

    private SuggestionResearcher Researcher(IConnectorTools tools) =>
        new(_model, tools, new FakeSettingsStore(new ClickySettings()), _time, NullLogger<SuggestionResearcher>.Instance);
}
