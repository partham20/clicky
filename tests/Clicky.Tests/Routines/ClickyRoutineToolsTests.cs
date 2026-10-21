using System.Text.Json;
using Clicky.Core;
using Clicky.Core.Actions;
using Clicky.Core.Agent;
using Clicky.Core.Assistant;
using Clicky.Core.Clickys;
using Clicky.Core.Routines;
using Clicky.Tests.Agent;
using Clicky.Tests.Clickys;
using Microsoft.Extensions.Logging.Abstractions;
using static Clicky.Tests.Routines.RoutineScheduleTests;

namespace Clicky.Tests.Routines;

public sealed class ClickyRoutineToolsTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Monday7 = Utc(2026, 1, 5, 7, 0);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clicky-routine-tools-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClickyModel _model = new();
    private readonly FakePresenter _presenter = new();
    private readonly List<IAsyncDisposable> _owned = [];
    private ClickyAgent _desk = null!;

    private EngineHarness H => _owned.OfType<EngineHarness>().Single();

    private ClickyService Service => _owned.OfType<ClickyService>().Single();

    public async Task InitializeAsync()
    {
        _owned.Add(new EngineHarness(Monday7));
        await H.StartAsync();
        var tools = new RoutineTools(H.Engine, H.Time, () => H.Zone, new RoutineConfirmer(_presenter, H.Time, () => H.Zone));
        _owned.Insert(0, new ClickyService(
            new ClickyPaths(_dir),
            _model,
            new ChatOnlyClickyActor(),
            NullLogger<ClickyService>.Instance,
            tools: new ClickyRoutineTools(new Lazy<RoutineTools>(() => tools))));
        _desk = await Service.CreateAsync(new ClickyDraft("PDF Desk"));
    }

    public async Task DisposeAsync()
    {
        foreach (var owned in _owned)
        {
            await owned.DisposeAsync();
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static UseToolAction Call(string tool, string input) => new(tool, JsonDocument.Parse(input).RootElement.Clone());

    /// <summary>A reply that says <paramref name="text"/> and makes one tool call, as the API backend streams it.</summary>
    private static object[] Calling(string text, ClickyAction action, string toolCallId)
    {
        var call = new ReplyToolCall(new ToolCallRecord(toolCallId, action is UseToolAction u ? u.Tool : action.Name, "{}"), action);
        return
        [
            Replies.Started(),
            new ReplyText(text),
            new ReplyAction(action),
            call,
            new ReplyCompleted(ReplyStop.Actions, [new SayAction(text), action], new ReplyUsage(10, 5, 0, 0), null, TimeSpan.Zero, Replies.Model, ToolCalls: [call]),
        ];
    }

    [Fact]
    public async Task A_chat_creates_its_own_routine_after_the_user_saves_it()
    {
        var create = Call(RoutineTools.Create, """{ "name": "Digest", "prompt": "Summarise new PDFs.", "schedule": { "kind": "weekdays", "time": "08:00" } }""");
        _model.Then(Calling("I'll set that up.", create, "toolu_1")).Then(Replies.Text("Done: weekdays at 8."));

        var outcome = await Service.RunAsync(_desk.Id, new ClickyRunRequest("Summarise new PDFs every weekday morning", ClickyRunTrigger.Chat));

        Assert.True(outcome.Succeeded, outcome.Summary);
        var routine = Assert.Single(H.Engine.Routines);
        Assert.Equal(("Digest", _desk.Id), (routine.Name, routine.ClickyId));
        Assert.Equal(RoutineConfirmer.SaveLabel, Assert.Single(_presenter.Last.Prompts).AllowLabel);

        var calls = _model.Calls;
        Assert.Equal(2, calls.Count);
        Assert.Equal(RoutineTools.For(attended: true).Select(t => t.Name), calls[0].Request.Tools!.Select(t => t.Name));
        var result = Assert.Single(calls[1].Request.Step!.Results);
        Assert.Equal(("toolu_1", RoutineTools.Create, false), (result.ToolCallId, result.Name, result.IsError));
        Assert.Contains("The user saved the routine", result.Text, StringComparison.Ordinal);
        Assert.NotNull(calls[1].Request.Tools);
        Assert.Contains("Done: weekdays at 8.", outcome.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claudes_routine_action_in_a_chat_is_a_create_and_not_now_saves_nothing()
    {
        _presenter.NewCard = () =>
        {
            var card = new FakeCard();
            card.Answers.Enqueue(PermissionAnswer.NotNow);
            return card;
        };
        var action = new RoutineAction("Digest", "Summarise new PDFs.", JsonDocument.Parse("""{ "kind": "daily", "time": "08:00" }""").RootElement.Clone());
        _model.Then(Calling("I'll set that up.", action, "toolu_9")).Then(Replies.Text("Okay, I won't."));

        await Service.RunAsync(_desk.Id, new ClickyRunRequest("Summarise new PDFs every morning", ClickyRunTrigger.Chat));

        Assert.Empty(H.Engine.Routines);
        var result = Assert.Single(_model.Calls[1].Request.Step!.Results);
        Assert.Equal(("toolu_9", "routine"), (result.ToolCallId, result.Name));
        Assert.Contains("chose not to save", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_routine_run_can_list_and_pause_but_never_create_or_delete()
    {
        var mine = await H.Engine.CreateAsync(new RoutineDraft("Digest", "Summarise new PDFs.", RoutineSchedule.Daily(new TimeOnly(9, 0)), _desk.Id));
        var delete = Call(RoutineTools.Delete, """{ "routine": "Digest" }""");
        var create = new RoutineAction("Another", "More.", JsonDocument.Parse("""{ "kind": "startup" }""").RootElement.Clone());
        _model.Then(Calling("Cleaning up.", delete, "toolu_1")).Then(Calling("Adding one.", create, "toolu_2")).Then(Replies.Text("I can't change routines from here."));

        await Service.RunAsync(_desk.Id, new ClickyRunRequest("Summarise new PDFs.", ClickyRunTrigger.Routine, mine.Id, mine.Name, Monday7, IsCatchUp: false));

        Assert.Equal([RoutineTools.List, RoutineTools.Pause], _model.Calls[0].Request.Tools!.Select(t => t.Name));
        Assert.True(Assert.Single(_model.Calls[1].Request.Step!.Results).IsError);
        Assert.True(Assert.Single(_model.Calls[2].Request.Step!.Results).IsError);
        Assert.Empty(_presenter.Cards);
        Assert.Equal([mine.Id], H.Engine.Routines.Select(r => r.Id));
    }

    [Fact]
    public async Task The_tool_loop_stops_at_the_clickys_step_cap()
    {
        await Service.UpdateAsync(_desk.Id, new ClickyDraft("PDF Desk", MaxSteps: 2));
        var list = Call(RoutineTools.List, "{}");
        _model.Default = Calling("Checking.", list, "toolu_x");

        await Service.RunAsync(_desk.Id, new ClickyRunRequest("What routines do you have?", ClickyRunTrigger.Chat));

        Assert.Equal(2, _model.Calls.Count);
    }
}
