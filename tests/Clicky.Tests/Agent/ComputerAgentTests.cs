using System.Text.Json;
using Clicky.Core.Actions;
using Clicky.Core.Agent;
using Clicky.Core.Assistant;
using Clicky.Core.Memory;
using Clicky.Core.Settings;
using Clicky.Tests.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using static Clicky.Tests.Agent.AgentTestData;

namespace Clicky.Tests.Agent;

public sealed class ComputerAgentTests
{
    private readonly FakeComputer _computer = new();
    private readonly FakeAgentCapture _capture = new();
    private readonly FakePresenter _presenter = new();
    private readonly RecordingMemory _memory = new();

    [Fact]
    public void Wants_to_act_only_for_actions_that_need_results()
    {
        var agent = Agent(new ScriptedAssistant());

        Assert.True(agent.WantsToAct(Reply(new ClickAction(1, 2))));
        Assert.True(agent.WantsToAct(Reply(new AskPermissionAction("Delete it"))));
        Assert.True(agent.WantsToAct(Reply(new UseToolAction("github__list", JsonDocument.Parse("{}").RootElement))));
        Assert.True(agent.WantsToAct(Calls(("t1", "screenshot", null))));
        Assert.False(agent.WantsToAct(Reply(new SayAction("Here."), new PointAction(1, 2))));
        Assert.False(agent.WantsToAct(Reply(new DoneAction())));
        Assert.False(agent.WantsToAct(Reply(new ClickAction(1, 2)) with { Stop = ReplyStop.Refused }));

        var rejected = new ReplyCompleted(ReplyStop.Actions, [], new ReplyUsage(0, 0, 0, 0), null, TimeSpan.Zero, "m", ToolCalls:
            [new ReplyToolCall(new ToolCallRecord("t1", "click", "{}", Accepted: false, Error: "x is required"), null)]);
        Assert.True(agent.WantsToAct(rejected));
        var rejectedPoint = rejected with { ToolCalls = [new ReplyToolCall(new ToolCallRecord("t1", "point", "{}", Accepted: false, Error: "bad"), null)] };
        Assert.False(agent.WantsToAct(rejectedPoint));
    }

    [Fact]
    public async Task First_action_is_asked_about_then_done_sends_the_results_and_ends_the_run()
    {
        var assistant = new ScriptedAssistant();
        var agent = Agent(assistant);
        var conversation = Conversation();

        var events = await RunAsync(agent, conversation, Reply(new SayAction("Clicking Save."), new ClickAction(100, 50, Target: "Save button")));

        var prompt = Assert.Single(_presenter.Last.Prompts);
        Assert.Equal("Click \"Save button\" in Notepad?", prompt.Question);
        Assert.True(prompt.OffersAlways);
        Assert.Empty(_presenter.Last.Countdowns); // Asked, so no countdown as well.
        Assert.Single(_computer.Executed);
        Assert.False(_computer.Executed[0].Options.AllowMouse);

        var request = Assert.Single(assistant.Requests);
        Assert.Equal(RequestSource.Agent, request.Source);
        Assert.NotNull(request.Screenshot);
        Assert.Equal(1, request.Step!.Number);
        Assert.Equal(["Did click."], request.Step.Results.Select(r => r.Text));
        Assert.Contains(events, e => e is ReplyScreenshotTaken);
        Assert.Contains(events, e => e is ReplyText { Text: "All done." });

        Assert.Equal(("Renamed it.", true), _presenter.Last.Finished);
        Assert.True(_presenter.Last.Disposed);
        Assert.Equal(["rename the file to notes"], _presenter.Tasks);
        var turn = Assert.Single(_memory.Turns);
        Assert.Equal(MemoryTurnMode.Agent, turn.Mode);
        Assert.Equal("Renamed it.", turn.Assistant);

        // "done" was handled but never sent; its result waits for the next question.
        Assert.Null(conversation.UnsentResults);
    }

    [Fact]
    public async Task Always_for_the_conversation_skips_the_question_but_the_next_run_counts_down()
    {
        var agent = Agent(new ScriptedAssistant());
        var conversation = Conversation();
        _presenter.NewCard = () =>
        {
            var card = new FakeCard();
            card.Answers.Enqueue(PermissionAnswer.AlwaysThisConversation);
            return card;
        };

        await RunAsync(agent, conversation, Reply(new ClickAction(10, 10)));
        Assert.Single(_presenter.Last.Prompts);
        Assert.True(agent.GateFor(conversation).AlwaysControl);

        await RunAsync(agent, conversation, Reply(new KeyAction("Ctrl+S")));
        Assert.Empty(_presenter.Last.Prompts);
        Assert.Equal(["Press Ctrl+S in Notepad"], _presenter.Last.Countdowns);
        Assert.Equal(2, _computer.Executed.Count);

        // A new conversation starts with nothing allowed.
        Assert.False(agent.GateFor(new Conversation()).AlwaysControl);
    }

    [Fact]
    public async Task Cancelling_the_countdown_does_nothing_and_ends_quietly()
    {
        var assistant = new ScriptedAssistant();
        var agent = Agent(assistant);
        var conversation = Conversation();
        agent.GateFor(conversation).Remember(PermissionKind.Control);
        _presenter.NewCard = () => new FakeCard { CountdownGoesAhead = false };

        await RunAsync(agent, conversation, Calls(("t1", "left_click", new ClickAction(10, 10))));

        Assert.Empty(_computer.Executed);
        Assert.Empty(assistant.Requests);
        Assert.Equal(("Cancelled.", true), _presenter.Last.Finished);
        var kept = Assert.Single(conversation.UnsentResults!);
        Assert.Equal("t1", kept.ToolCallId);
        Assert.True(kept.IsError);
    }

    [Fact]
    public async Task Irreversible_actions_are_always_asked_and_never_offer_always()
    {
        var agent = Agent(new ScriptedAssistant());
        var conversation = Conversation();
        agent.GateFor(conversation).Remember(PermissionKind.Control);
        _presenter.NewCard = () =>
        {
            var card = new FakeCard();
            card.Answers.Enqueue(PermissionAnswer.AlwaysThisConversation); // A buggy card can't make it "always".
            return card;
        };
        _computer.Target = _ => new ControlTarget("Send", "button", "Outlook", "olk");

        await RunAsync(agent, conversation, Reply(new ClickAction(5, 5)));

        var prompt = Assert.Single(_presenter.Last.Prompts);
        Assert.True(prompt.Irreversible);
        Assert.False(prompt.OffersAlways);
        Assert.Contains("can't be undone", prompt.Detail, StringComparison.Ordinal);
        Assert.Single(_computer.Executed);

        await RunAsync(agent, conversation, Reply(new ClickAction(5, 5)));
        Assert.Single(_presenter.Last.Prompts);
    }

    [Fact]
    public async Task Not_now_runs_nothing_and_lets_the_model_acknowledge_once()
    {
        var assistant = new ScriptedAssistant(Reply(new SayAction("Okay."), new ClickAction(1, 1)));
        var agent = Agent(assistant);
        _presenter.NewCard = () =>
        {
            var card = new FakeCard();
            card.Answers.Enqueue(PermissionAnswer.NotNow);
            return card;
        };

        await RunAsync(agent, Conversation(), Reply(new ClickAction(1, 1), new TypeAction("notes")));

        Assert.Empty(_computer.Executed);
        var request = Assert.Single(assistant.Requests); // The model's next click isn't acted on.
        Assert.Contains("not now", request.Text, StringComparison.Ordinal);
        Assert.All(request.Step!.Results, r => Assert.True(r.IsError));
        Assert.Equal(("Okay, not doing that.", true), _presenter.Last.Finished);
    }

    [Fact]
    public async Task The_step_budget_stops_a_model_that_keeps_going()
    {
        var assistant = new ScriptedAssistant(Reply(new ClickAction(1, 1)), Reply(new ClickAction(2, 2)), Reply(new ClickAction(3, 3)));
        var agent = Agent(assistant, new AgentSettings(MaxSteps: 2));

        await RunAsync(agent, Conversation(), Reply(new ClickAction(0, 0)));

        Assert.Equal(2, _computer.Executed.Count);
        Assert.Equal(2, assistant.Requests.Count);
        Assert.Contains("last step allowed (2)", assistant.Requests[^1].Text, StringComparison.Ordinal);
        Assert.Equal(("Stopped after 2 steps.", false), _presenter.Last.Finished);
    }

    [Fact]
    public async Task No_ui_automation_way_asks_to_use_the_mouse_and_retries_with_it()
    {
        var agent = Agent(new ScriptedAssistant());
        _computer.Outcomes.Enqueue(new ControlOutcome(ControlStatus.NeedsMouse, "No pattern."));

        await RunAsync(agent, Conversation(), Reply(new ClickAction(10, 10, MouseButton.Right)));

        Assert.Equal(2, _presenter.Last.Prompts.Count);
        Assert.Equal(PermissionKind.Mouse, _presenter.Last.Prompts[1].Kind);
        Assert.StartsWith("Let Clicky use your mouse to right-click", _presenter.Last.Prompts[1].Question, StringComparison.Ordinal);
        Assert.Equal([false, true], _computer.Executed.Select(e => e.Options.AllowMouse));
    }

    [Fact]
    public async Task A_failed_action_skips_the_rest_of_the_batch_with_the_toolset_message()
    {
        var assistant = new ScriptedAssistant();
        var agent = Agent(assistant);
        _computer.Outcomes.Enqueue(ControlOutcome.Failed("Nothing there."));

        await RunAsync(agent, Conversation(), Calls(("t1", "left_click", new ClickAction(1, 1)), ("t2", "type", new TypeAction("hi")), ("t3", "screenshot", null)));

        Assert.Single(_computer.Executed);
        var results = assistant.Requests[0].Step!.Results;
        Assert.Equal(["t1", "t2", "t3"], results.Select(r => r.ToolCallId));
        Assert.Equal("Nothing there.", results[0].Text);
        Assert.Equal(ComputerToolset.SkippedAfterFailure, results[1].Text);
        Assert.Equal(ComputerAgent.ScreenshotResult, results[2].Text);
        Assert.Equal([true, true, false], results.Select(r => r.IsError));
    }

    [Fact]
    public async Task Read_only_connector_tools_run_without_asking_and_others_are_asked_about()
    {
        var tools = new FakeConnectorTools(Tool("github__list_issues", readOnly: true), Tool("github__create_issue", readOnly: false));
        var assistant = new ScriptedAssistant();
        var agent = Agent(assistant, connectors: tools);
        var input = JsonDocument.Parse("""{"repo":"clicky"}""").RootElement;

        await RunAsync(agent, Conversation(), Reply(new UseToolAction("github__list_issues", input)));
        Assert.Empty(_presenter.Last.Prompts);
        Assert.Empty(_presenter.Last.Countdowns);
        Assert.Equal("3 open issues", assistant.Requests[^1].Step!.Results[0].Text);

        await RunAsync(agent, Conversation(), Reply(new UseToolAction("github__create_issue", input)));
        var prompt = Assert.Single(_presenter.Last.Prompts);
        Assert.Equal(PermissionKind.Connector, prompt.Kind);
        Assert.Equal("Use create_issue in GitHub (partha)?", prompt.Question);
        Assert.Contains("clicky", prompt.Detail, StringComparison.Ordinal);
        Assert.Equal(["github__list_issues", "github__create_issue"], tools.Called);
    }

    [Fact]
    public async Task Unattended_runs_without_a_prompter_refuse_what_needs_asking()
    {
        var assistant = new ScriptedAssistant(Reply(new SayAction("Okay.")));
        var agent = Agent(assistant);

        await RunAsync(agent, Conversation(), Reply(new ClickAction(1, 1)), new AgentRunOptions(Unattended: true));

        Assert.Empty(_presenter.Cards);
        Assert.Empty(_computer.Executed);
        Assert.Contains("nobody is there", assistant.Requests[0].Step!.Results[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clicky_set_to_use_the_computer_acts_alone_but_never_on_irreversible_steps()
    {
        var assistant = new ScriptedAssistant(Reply(new ClickAction(2, 2, Target: "Send")));
        var actor = new ComputerAgentClickyActor(Agent(assistant));
        _computer.Target = a => a is ClickAction { Target: "Send" } ? new ControlTarget("Send", "button", "Outlook", "olk") : new ControlTarget("New message", "button", "Outlook", "olk");
        var first = Reply(new ClickAction(1, 1, Target: "New message"));

        Assert.True(actor.WantsToAct(first));
        var events = new List<ReplyEvent>();
        await foreach (var replyEvent in actor.ContinueAsync(Conversation(), first, Shot, new Clicky.Core.Clickys.ClickyActorOptions(5), CancellationToken.None))
        {
            events.Add(replyEvent);
        }

        Assert.Empty(_presenter.Cards);
        Assert.Equal([new ClickAction(1, 1, Target: "New message")], _computer.Executed.Select(e => e.Action));
        Assert.Contains("nobody is there", assistant.Requests[1].Step!.Results[0].Text, StringComparison.Ordinal);
        Assert.Empty(_memory.Turns); // The Clicky keeps its own record.
        Assert.Equal(2, events.OfType<ReplyCompleted>().Count());
    }

    [Fact]
    public async Task Administrator_windows_and_a_disabled_agent_are_refused_without_acting()
    {
        var assistant = new ScriptedAssistant();
        var agent = Agent(assistant);
        _computer.Target = _ => new ControlTarget("OK", "button", "Registry Editor", "regedit", Elevated: true);

        await RunAsync(agent, Conversation(), Reply(new ClickAction(1, 1)));
        Assert.Empty(_computer.Executed);
        Assert.Contains("runs as administrator", assistant.Requests[0].Step!.Results[0].Text, StringComparison.Ordinal);

        var off = new ScriptedAssistant();
        await RunAsync(Agent(off, new AgentSettings(Enabled: false)), Conversation(), Reply(new TypeAction("x")));
        Assert.Empty(_computer.Executed);
        Assert.Contains("switched off", off.Requests[0].Step!.Results[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_ends_the_run_quietly_and_keeps_what_already_ran()
    {
        var assistant = new ScriptedAssistant();
        var agent = Agent(assistant);
        var conversation = Conversation();
        _computer.OnExecute = action =>
        {
            if (action is TypeAction)
            {
                _presenter.Last.Stop();
            }
        };

        var events = await RunAsync(agent, conversation, Calls(("t1", "left_click", new ClickAction(1, 1)), ("t2", "type", new TypeAction("notes"))));

        Assert.Empty(assistant.Requests);
        Assert.DoesNotContain(events, e => e is ReplyScreenshotTaken);
        Assert.Equal(("Stopped.", true), _presenter.Last.Finished);
        Assert.Equal(["t1"], conversation.UnsentResults!.Select(r => r.ToolCallId));
    }

    [Fact]
    public async Task A_yes_to_the_models_own_question_covers_its_next_step()
    {
        var assistant = new ScriptedAssistant(Reply(new ClickAction(1, 1, Target: "Delete")));
        var agent = Agent(assistant);
        _computer.Target = _ => new ControlTarget("Delete", "button", "Explorer", "explorer");

        await RunAsync(agent, Conversation(), Reply(new AskPermissionAction("Delete old.txt", Risk: ActionRisk.Irreversible)));

        var prompt = Assert.Single(_presenter.Last.Prompts);
        Assert.Equal(PermissionKind.Question, prompt.Kind);
        Assert.Equal("Delete old.txt?", prompt.Question);
        Assert.Single(_computer.Executed); // The Delete click itself was not asked about again.
        Assert.Equal("The user said yes. Go ahead.", assistant.Requests[0].Step!.Results[0].Text);
    }

    [Fact]
    public void Task_text_is_the_users_words_without_context_lines()
    {
        var conversation = Conversation("open my GitHub notifications");
        conversation.Append(new ConversationTurn("[Agent step 1 of at most 25.]\nCarry on.", null, "ok", [], DateTimeOffset.UnixEpoch), new HistoryLimits());

        Assert.Equal("open my GitHub notifications", ComputerAgent.TaskText(conversation));
    }

    private ComputerAgent Agent(ScriptedAssistant assistant, AgentSettings? agent = null, FakeConnectorTools? connectors = null) =>
        new(
            assistant,
            _computer,
            _capture,
            _presenter,
            new FakeSettingsStore(new ClickySettings { Agent = agent ?? new AgentSettings() }),
            NullLogger<ComputerAgent>.Instance,
            connectors,
            _memory,
            new InstantTime());

    private static async Task<List<ReplyEvent>> RunAsync(ComputerAgent agent, Conversation conversation, ReplyCompleted first, AgentRunOptions? options = null)
    {
        var events = new List<ReplyEvent>();
        await foreach (var replyEvent in agent.ContinueAsync(conversation, first, Shot, options ?? AgentRunOptions.Default))
        {
            events.Add(replyEvent);
        }

        return events;
    }
}
