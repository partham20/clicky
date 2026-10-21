using System.Collections.Concurrent;
using System.Diagnostics;
using Clicky.Core;
using Clicky.Core.Actions;
using Clicky.Core.Assistant;
using Clicky.Core.Clickys;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Clickys;

public sealed class ClickyServiceTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clicky-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClickyModel _model = new();
    private readonly List<ClickyService> _services = [];
    private readonly ConcurrentQueue<ClickyChangedEventArgs> _changes = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ClickyRunFinishedEventArgs>> _finished = new(StringComparer.Ordinal);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.DisposeAsync();
        }

        ClickyDatabase.ReleaseFiles();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Create_normalizes_the_draft()
    {
        var service = CreateService();

        var clicky = await service.CreateAsync(new ClickyDraft("  Inbox helper  ", "  Sort mail.  ", " Dana is the boss. ", "#abcdef", UsesComputer: true, MaxSteps: 1000));

        Assert.Equal(32, clicky.Id.Length);
        Assert.Equal("Inbox helper", clicky.Name);
        Assert.Equal("Sort mail.", clicky.Instructions);
        Assert.Equal("Dana is the boss.", clicky.Notes);
        Assert.Equal("#ABCDEF", clicky.Color);
        Assert.True(clicky.UsesComputer);
        Assert.Equal(ClickyDraft.MaxMaxSteps, clicky.MaxSteps);
        Assert.Equal(0, clicky.Unread);
        // Stored times have millisecond precision; the returned record is not rounded (see the report).
        Assert.Equal(Stored(clicky), await service.GetAsync(clicky.Id));
        Assert.Equal([Stored(clicky)], await service.ListAsync());

        var odd = await service.CreateAsync(new ClickyDraft(new string('n', 100), Color: "blue", MaxSteps: 0));
        Assert.Equal(ClickyDraft.MaxNameLength, odd.Name.Length);
        Assert.Equal(ClickyDraft.DefaultColor, odd.Color);
        Assert.Equal(ClickyDraft.MinMaxSteps, odd.MaxSteps);
        Assert.Equal(ClickyDraft.DefaultColor, (await service.CreateAsync(new ClickyDraft("x", Color: "#12345G"))).Color);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new ClickyDraft("   ")));
        Assert.Equal(3, (await service.ListAsync()).Count);
        Assert.Contains(_changes, e => e.Change == ClickyChange.ClickyAdded && e.ClickyId == clicky.Id);
    }

    [Fact]
    public async Task Update_normalizes_and_unknown_clickys_are_not_found()
    {
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Before"));

        var updated = await service.UpdateAsync(clicky.Id, ClickyDraft.From(clicky) with { Name = " After ", Notes = " remember this ", MaxSteps = -5 });

        Assert.Equal("After", updated.Name);
        Assert.Equal("remember this", updated.Notes);
        Assert.Equal(ClickyDraft.MinMaxSteps, updated.MaxSteps);
        Assert.Equal(Ms(clicky.CreatedAt), updated.CreatedAt);
        Assert.Contains(_changes, e => e.Change == ClickyChange.ClickyUpdated && e.ClickyId == clicky.Id);

        await Assert.ThrowsAsync<ClickyNotFoundException>(() => service.UpdateAsync("ffff", new ClickyDraft("x")));
        await Assert.ThrowsAsync<ClickyNotFoundException>(() => service.SendAsync("ffff", "hello"));
        await Assert.ThrowsAsync<ClickyNotFoundException>(() => service.RunAsync("ffff", new ClickyRunRequest("hello")));
        await Assert.ThrowsAsync<ClickyNotFoundException>(() => service.RetryAsync("nope"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SendAsync(clicky.Id, "  "));
        Assert.False(service.Pause("nope"));
        Assert.False(service.Resume("nope"));
        Assert.False(service.Cancel("nope"));
        Assert.False(service.IsPauseRequested("nope"));
    }

    [Fact]
    public async Task Send_posts_the_message_runs_and_posts_the_reply_as_unread()
    {
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Weather"));
        _model.Then(Replies.Text("**Sunny** and warm.\n\nMore detail below.", new ReplyUsage(100, 20, 5, 3, 0.01m)));

        var run = await service.SendAsync(clicky.Id, "  What's the weather?  ");
        Assert.Equal(ClickyRunTrigger.Chat, run.Trigger);
        Assert.Equal("What's the weather?", run.Prompt);

        var finished = await FinishedAsync(run.Id);

        Assert.Equal(ClickyRunStatus.Completed, finished.Outcome.Status);
        Assert.True(finished.Outcome.Succeeded);
        Assert.Equal("Sunny and warm.", finished.Outcome.Summary);
        Assert.Equal("**Sunny** and warm.\n\nMore detail below.", finished.Outcome.Reply);
        Assert.Equal(clicky.Id, finished.Clicky.Id);

        var messages = await service.GetMessagesAsync(clicky.Id);
        Assert.Equal(
            [(ClickyMessageRole.User, "What's the weather?"), (ClickyMessageRole.Assistant, "**Sunny** and warm.\n\nMore detail below.")],
            messages.Select(m => (m.Role, m.Text)));
        Assert.All(messages, m => Assert.Equal(run.Id, m.RunId));
        Assert.Equal(1, (await service.GetAsync(clicky.Id))!.Unread);

        var stored = await service.GetRunAsync(run.Id);
        Assert.NotNull(stored);
        Assert.Equal(ClickyRunStatus.Completed, stored.Status);
        Assert.Equal(1, stored.Steps);
        Assert.Equal(Replies.Model, stored.Model);
        Assert.Equal(108, stored.InputTokens);
        Assert.Equal(20, stored.OutputTokens);
        Assert.Equal(0.01m, stored.CostUsd);
        Assert.Equal("Sunny and warm.", stored.Summary);
        Assert.Null(stored.Error);
        Assert.NotNull(stored.StartedAt);
        Assert.NotNull(stored.EndedAt);
        Assert.Equal(stored, Stored(finished.Run));

        var (conversation, request) = Assert.Single(_model.Calls);
        Assert.Equal("What's the weather?", request.Text);
        Assert.Equal(RequestSource.Agent, request.Source);
        Assert.Null(request.Screenshot);
        Assert.Empty(conversation.Turns);

        var steps = await service.GetStepsAsync(run.Id);
        Assert.Equal([(ClickyStepKind.Request, "Asked Claude"), (ClickyStepKind.Text, "**Sunny** and warm.\n\nMore detail below.")], steps.Select(s => (s.Kind, s.Text)));
        Assert.Empty(service.ActiveRuns);

        await service.MarkReadAsync(clicky.Id);
        Assert.Equal(0, (await service.GetAsync(clicky.Id))!.Unread);
    }

    [Fact]
    public async Task Changed_and_run_finished_events_fire_for_a_run()
    {
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Events"));
        var finishes = new ConcurrentQueue<ClickyRunFinishedEventArgs>();
        service.RunFinished += (_, e) => finishes.Enqueue(e);

        var run = await service.SendAsync(clicky.Id, "hi");
        await FinishedAsync(run.Id);

        var forRun = _changes.Where(e => e.RunId == run.Id).ToList();
        Assert.All(forRun, e => Assert.Equal(clicky.Id, e.ClickyId));
        Assert.Equal(2, forRun.Count(e => e.Change == ClickyChange.MessageAdded));
        Assert.Contains(forRun, e => e.Change == ClickyChange.RunChanged);
        Assert.Contains(forRun, e => e.Change == ClickyChange.StepAdded);
        Assert.Equal(ClickyChange.RunChanged, forRun[^1].Change);
        Assert.Equal(run.Id, Assert.Single(finishes).Run.Id);
    }

    [Fact]
    public async Task Routine_run_posts_a_routine_message_and_returns_the_outcome()
    {
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Digest"));
        _model.Then(Replies.Text("## Inbox\n\nThree new emails, none urgent."));
        var due = new DateTimeOffset(2026, 10, 21, 7, 0, 0, TimeSpan.Zero);

        var outcome = await service.RunAsync(clicky.Id, new ClickyRunRequest("  Summarise my inbox  ", ClickyRunTrigger.Routine, "r1", "Morning digest", due, IsCatchUp: true));

        Assert.Equal(ClickyRunStatus.Completed, outcome.Status);
        Assert.True(outcome.Succeeded);
        Assert.Equal("Inbox", outcome.Summary);
        Assert.Equal("## Inbox\n\nThree new emails, none urgent.", outcome.Reply);

        var messages = await service.GetMessagesAsync(clicky.Id);
        Assert.Equal([ClickyMessageRole.Routine, ClickyMessageRole.Assistant], messages.Select(m => m.Role));
        Assert.Equal("Summarise my inbox", messages[0].Text);

        var request = Assert.Single(_model.Calls).Request;
        Assert.StartsWith("(Routine \"Morning digest\", scheduled for", request.Text, StringComparison.Ordinal);
        Assert.Contains("running late", request.Text, StringComparison.Ordinal);
        Assert.EndsWith("\n\nSummarise my inbox", request.Text, StringComparison.Ordinal);

        var run = await service.GetRunAsync(outcome.RunId);
        Assert.NotNull(run);
        Assert.Equal(ClickyRunTrigger.Routine, run.Trigger);
        Assert.Equal("r1", run.RoutineId);
        Assert.Equal("Morning digest", run.RoutineName);
        Assert.Equal(due, run.ScheduledFor);
        Assert.Equal("Summarise my inbox", run.Prompt);
    }

    [Fact]
    public async Task Done_with_failure_is_not_a_success()
    {
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Login"));
        _model.Then(Replies.Text("I tried to sign in.", null, new DoneAction("The password was rejected", Success: false)));

        var outcome = await service.RunAsync(clicky.Id, new ClickyRunRequest("Sign in", ClickyRunTrigger.Manual));

        Assert.Equal(ClickyRunStatus.Completed, outcome.Status);
        Assert.False(outcome.Succeeded);
        Assert.Equal("The password was rejected", outcome.Summary);
        Assert.Equal("I tried to sign in.", outcome.Reply);
    }

    [Fact]
    public async Task Done_summary_stands_in_for_an_empty_reply()
    {
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Quiet"));
        _model.Then(Replies.Started(), new ReplyAction(new DoneAction("Filed 3 receipts")), Replies.Completed(ReplyStop.EndTurn, null, new DoneAction("Filed 3 receipts")));

        var outcome = await service.RunAsync(clicky.Id, new ClickyRunRequest("File receipts"));

        Assert.True(outcome.Succeeded);
        Assert.Equal("Filed 3 receipts", outcome.Reply);
        Assert.Equal("Filed 3 receipts", outcome.Summary);
        Assert.Contains(await service.GetStepsAsync(outcome.RunId), s => s.Kind == ClickyStepKind.Action && s.Text == "done: Filed 3 receipts");
    }

    [Fact]
    public async Task Refused_reply_is_not_a_success()
    {
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Careful"));
        _model.Then(Replies.Started(), new ReplyText("I can't help with that."), Replies.Completed(ReplyStop.Refused));

        var outcome = await service.RunAsync(clicky.Id, new ClickyRunRequest("Something bad", ClickyRunTrigger.Chat));

        Assert.Equal(ClickyRunStatus.Completed, outcome.Status);
        Assert.False(outcome.Succeeded);
        Assert.Contains(await service.GetStepsAsync(outcome.RunId), s => s.Kind == ClickyStepKind.Error && s.Text == "Claude declined this request");
    }

    [Fact]
    public async Task Backend_failure_fails_the_run_and_is_rethrown()
    {
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Limited"));
        _model.Then(Replies.Started(), new ClaudeBackendException(ClaudeFailure.RateLimited, "You've reached your usage limit."));

        var ex = await Assert.ThrowsAsync<ClaudeBackendException>(() => service.RunAsync(clicky.Id, new ClickyRunRequest("Do the thing")));

        Assert.Equal(ClaudeFailure.RateLimited, ex.Failure);
        var run = Assert.Single(await service.GetRunsAsync(clicky.Id));
        Assert.Equal(ClickyRunStatus.Failed, run.Status);
        Assert.Equal("You've reached your usage limit.", run.Error);
        Assert.NotNull(run.EndedAt);

        var messages = await service.GetMessagesAsync(clicky.Id);
        Assert.Equal(ClickyMessageRole.System, messages[^1].Role);
        Assert.Equal("Couldn't finish: You've reached your usage limit.", messages[^1].Text);
        Assert.Equal(1, (await service.GetAsync(clicky.Id))!.Unread);

        var finished = await FinishedAsync(run.Id);
        Assert.Equal(ClickyRunStatus.Failed, finished.Outcome.Status);
        Assert.False(finished.Outcome.Succeeded);
    }

    [Fact]
    public async Task Unexpected_failure_fails_the_run_without_throwing()
    {
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Buggy"));
        _model.Then(new InvalidOperationException("boom"));

        var outcome = await service.RunAsync(clicky.Id, new ClickyRunRequest("Go"));

        Assert.Equal(ClickyRunStatus.Failed, outcome.Status);
        Assert.False(outcome.Succeeded);
        Assert.Equal("Couldn't finish: Something went wrong: boom", (await service.GetMessagesAsync(clicky.Id))[^1].Text);
    }

    [Fact]
    public async Task Cancel_mid_run_keeps_what_was_said()
    {
        var gate = new Gate();
        var actor = new FakeClickyActor(new ActorStep("Clicking Start.", [new ClickAction(5, 5)], gate));
        var service = CreateService(actor);
        var clicky = await service.CreateAsync(new ClickyDraft("Hands", UsesComputer: true));
        _model.Then(Replies.Started(), new ReplyText("Opening the app."), Replies.Completed(ReplyStop.Actions, null, new ClickAction(1, 1)));

        var run = await service.SendAsync(clicky.Id, "Open the app");
        await gate.Reached.WaitAsync(Timeout);
        Assert.True(service.Cancel(run.Id));
        var finished = await FinishedAsync(run.Id);

        Assert.True(gate.Cancelled);
        Assert.Equal(ClickyRunStatus.Cancelled, finished.Outcome.Status);
        Assert.False(finished.Outcome.Succeeded);
        Assert.Equal("Stopped.", finished.Outcome.Summary);
        Assert.Equal("Opening the app.", finished.Outcome.Reply);
        Assert.Equal(
            [(ClickyMessageRole.User, "Open the app"), (ClickyMessageRole.Assistant, "Opening the app.")],
            (await service.GetMessagesAsync(clicky.Id)).Select(m => (m.Role, m.Text)));

        var stored = await service.GetRunAsync(run.Id);
        Assert.Equal(ClickyRunStatus.Cancelled, stored!.Status);
        Assert.NotNull(stored.EndedAt);
        Assert.Equal((ClickyStepKind.Control, "Stopped."), (await service.GetStepsAsync(run.Id)).Select(s => (s.Kind, s.Text)).Last());
        Assert.False(service.Cancel(run.Id));
    }

    [Fact]
    public async Task Cancel_before_any_reply_posts_nothing()
    {
        var gate = new Gate();
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Slow"));
        _model.Then(Replies.Started(), gate, Replies.Completed());

        var run = await service.SendAsync(clicky.Id, "Take your time");
        await gate.Reached.WaitAsync(Timeout);
        Assert.True(service.Cancel(run.Id));
        var finished = await FinishedAsync(run.Id);

        Assert.True(gate.Cancelled);
        Assert.Equal(ClickyRunStatus.Cancelled, finished.Outcome.Status);
        Assert.Null(finished.Outcome.Reply);
        Assert.Equal([ClickyMessageRole.User], (await service.GetMessagesAsync(clicky.Id)).Select(m => m.Role));
    }

    // See the report: text streamed in the current step only reaches the reply at ReplyCompleted,
    // so a stop (or failure) mid-step drops it although Cancel promises to keep it.
    [Fact]
    public async Task Cancel_mid_step_keeps_the_text_streamed_so_far()
    {
        var gate = new Gate();
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Streamer"));
        _model.Then(Replies.Started(), new ReplyText("Here is the first half"), gate, Replies.Completed());

        var run = await service.SendAsync(clicky.Id, "Write something long");
        await gate.Reached.WaitAsync(Timeout);
        service.Cancel(run.Id);
        var finished = await FinishedAsync(run.Id);

        Assert.Equal(ClickyRunStatus.Cancelled, finished.Outcome.Status);
        Assert.Equal("Here is the first half", finished.Outcome.Reply);
    }

    [Fact]
    public async Task Caller_cancellation_stops_a_routine_run()
    {
        var gate = new Gate();
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Stoppable"));
        _model.Then(Replies.Started(), gate, Replies.Completed());
        using var cts = new CancellationTokenSource();

        var running = service.RunAsync(clicky.Id, new ClickyRunRequest("Long task"), cts.Token);
        await gate.Reached.WaitAsync(Timeout);
        await cts.CancelAsync();
        var outcome = await running.WaitAsync(Timeout);

        Assert.Equal(ClickyRunStatus.Cancelled, outcome.Status);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task Runs_of_one_clicky_go_one_at_a_time_with_history()
    {
        var gate = new Gate();
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Serial"));
        _model
            .Then(Replies.Started(), new ReplyText("First answer."), gate, Replies.Completed())
            .Then(Replies.Text("Second answer."));

        var first = await service.SendAsync(clicky.Id, "First question");
        await gate.Reached.WaitAsync(Timeout);
        var second = await service.SendAsync(clicky.Id, "Second question");

        Assert.Equal(ClickyRunStatus.Queued, (await service.GetRunAsync(second.Id))!.Status);
        Assert.Equal(ClickyRunStatus.Running, (await service.GetRunAsync(first.Id))!.Status);
        Assert.Equal(new[] { first.Id, second.Id }.Order(StringComparer.Ordinal), service.ActiveRuns.Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.Single(_model.Calls);

        gate.Open();
        await FinishedAsync(first.Id);
        await FinishedAsync(second.Id);

        Assert.Equal(2, _model.Calls.Count);
        Assert.Empty(_model.Calls[0].Conversation.Turns);
        Assert.Equal("Second question", _model.Calls[1].Request.Text);
        var turn = Assert.Single(_model.Calls[1].Conversation.Turns);
        Assert.Equal("First question", turn.UserText);
        Assert.Equal("First answer.", turn.AssistantText);
        Assert.Equal(2, (await service.GetAsync(clicky.Id))!.Unread);
    }

    [Fact]
    public async Task Different_clickys_run_side_by_side()
    {
        var gate = new Gate();
        var service = CreateService();
        var slow = await service.CreateAsync(new ClickyDraft("Slow"));
        var fast = await service.CreateAsync(new ClickyDraft("Fast"));
        _model.Then(Replies.Started(), gate, Replies.Completed()).Then(Replies.Text("Quick."));

        var held = await service.SendAsync(slow.Id, "Wait");
        await gate.Reached.WaitAsync(Timeout);
        var quick = await service.SendAsync(fast.Id, "Go");

        Assert.Equal(ClickyRunStatus.Completed, (await FinishedAsync(quick.Id)).Outcome.Status);
        Assert.Equal(ClickyRunStatus.Running, (await service.GetRunAsync(held.Id))!.Status);
        gate.Open();
        await FinishedAsync(held.Id);
    }

    [Fact]
    public async Task Context_carries_instructions_notes_and_files()
    {
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Briefing", "Always answer in French.", "The user's cat is called Miso."));
        var folder = service.Workspace.DirectoryFor(clicky.Id);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "brief.md"), "Project Falcon ships Friday.");

        var run = await service.SendAsync(clicky.Id, "What's due?");
        await FinishedAsync(run.Id);

        var memory = Assert.Single(_model.Calls).Conversation.Memory;
        Assert.NotNull(memory);
        Assert.StartsWith("# You are a Clicky", memory, StringComparison.Ordinal);
        Assert.Contains("\"Briefing\"", memory, StringComparison.Ordinal);
        Assert.Contains("Always answer in French.", memory, StringComparison.Ordinal);
        Assert.Contains("The user's cat is called Miso.", memory, StringComparison.Ordinal);
        Assert.Contains("<file name=\"brief.md\">\nProject Falcon ships Friday.\n</file>", memory, StringComparison.Ordinal);
        Assert.Contains("can't see or control the screen", memory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Computer_clicky_without_computer_use_chats_without_a_screenshot()
    {
        var service = CreateService(new ChatOnlyClickyActor());
        var clicky = await service.CreateAsync(new ClickyDraft("Wants hands", UsesComputer: true));
        _model.Then(Replies.Started(), new ReplyText("I'd click it."), Replies.Completed(ReplyStop.Actions, null, new ClickAction(1, 1)));

        var outcome = await service.RunAsync(clicky.Id, new ClickyRunRequest("Click it", ClickyRunTrigger.Chat));

        Assert.True(outcome.Succeeded);
        var (conversation, request) = Assert.Single(_model.Calls);
        Assert.Null(request.Screenshot);
        Assert.Contains("Computer use isn't available in this version of Clicky yet", conversation.Memory, StringComparison.Ordinal);
        Assert.DoesNotContain("You may see the screen", conversation.Memory, StringComparison.Ordinal);
        Assert.Equal(1, (await service.GetRunAsync(outcome.RunId))!.Steps);
    }

    [Fact]
    public async Task Actor_is_not_used_for_a_clicky_without_the_computer()
    {
        var actor = new FakeClickyActor(new ActorStep("Should not happen."));
        var service = CreateService(actor);
        var clicky = await service.CreateAsync(new ClickyDraft("No hands"));
        _model.Then(Replies.Started(), new ReplyText("Just chat."), Replies.Completed(ReplyStop.Actions, null, new ClickAction(1, 1)));

        var outcome = await service.RunAsync(clicky.Id, new ClickyRunRequest("Hi", ClickyRunTrigger.Chat));

        Assert.Equal("Just chat.", outcome.Reply);
        Assert.Equal(0, actor.Calls);
    }

    [Fact]
    public async Task Run_left_running_by_a_previous_session_is_interrupted_at_start()
    {
        Directory.CreateDirectory(_dir);
        var store = new ClickyStore(await ClickyDatabase.OpenAsync(Path.Combine(_dir, ClickyDatabase.FileName), NullLogger.Instance));
        var now = DateTimeOffset.UtcNow;
        var clicky = new ClickyAgent(Guid.NewGuid().ToString("N"), "Crashed", "", "", ClickyDraft.DefaultColor, false, 30, 0, now, now, now);
        await store.InsertClickyAsync(clicky);
        await store.InsertRunAsync(new ClickyRun("left", clicky.Id, ClickyRunTrigger.Chat, "p", ClickyRunStatus.Running, now, StartedAt: now));
        await store.InsertRunAsync(new ClickyRun("done", clicky.Id, ClickyRunTrigger.Chat, "p", ClickyRunStatus.Completed, now, StartedAt: now, EndedAt: now));

        var service = CreateService();
        await service.InitializeAsync();

        var left = await service.GetRunAsync("left");
        Assert.Equal(ClickyRunStatus.Interrupted, left!.Status);
        Assert.Equal("Clicky closed while this was running.", left.Error);
        Assert.NotNull(left.EndedAt);
        Assert.Equal(ClickyRunStatus.Completed, (await service.GetRunAsync("done"))!.Status);
        Assert.Empty(service.ActiveRuns);

        // The Clicky itself still works.
        var outcome = await service.RunAsync(clicky.Id, new ClickyRunRequest("Again", ClickyRunTrigger.Chat));
        Assert.Equal(ClickyRunStatus.Completed, outcome.Status);
    }

    [Fact]
    public async Task Pause_holds_the_run_between_actor_steps_until_resumed()
    {
        var gate = new Gate();
        var actor = new FakeClickyActor(
            new ActorStep("Opening Settings.", [new ClickAction(10, 20)], gate),
            new ActorStep("All done.", [new DoneAction("Changed the wallpaper")], Stop: ReplyStop.EndTurn));
        var service = CreateService(actor);
        var clicky = await service.CreateAsync(new ClickyDraft("Hands", UsesComputer: true, MaxSteps: 10));
        var first = Replies.Completed(ReplyStop.Actions, null, new ClickAction(1, 2));
        _model.Then(Replies.Started(), new ReplyText("Let me look."), new ReplyAction(new ClickAction(1, 2)), first);

        var run = await service.SendAsync(clicky.Id, "Change my wallpaper");
        await gate.Reached.WaitAsync(Timeout);
        Assert.True(service.Pause(run.Id));
        Assert.True(service.IsPauseRequested(run.Id));
        Assert.False(service.Pause(run.Id));
        Assert.False(service.Resume("nope"));
        gate.Open();

        var paused = await WaitForStatusAsync(service, run.Id, ClickyRunStatus.Paused);
        Assert.Equal(2, paused.Steps);
        Assert.Equal(1, actor.StepsStarted);
        Assert.Contains(_changes, e => e.Change == ClickyChange.RunChanged && e.RunId == run.Id);

        Assert.True(service.Resume(run.Id));
        Assert.False(service.Resume(run.Id));
        var finished = await FinishedAsync(run.Id);

        Assert.Equal(ClickyRunStatus.Completed, finished.Outcome.Status);
        Assert.True(finished.Outcome.Succeeded);
        Assert.Equal("Changed the wallpaper", finished.Outcome.Summary);
        Assert.Equal("Let me look.\n\nOpening Settings.\n\nAll done.", finished.Outcome.Reply);
        Assert.Equal(2, actor.StepsStarted);
        Assert.Equal(3, finished.Run.Steps);

        Assert.Equal(1, actor.Calls);
        Assert.Null(actor.Screenshot);
        Assert.Equal(new ClickyActorOptions(9), actor.Options);
        Assert.Same(first, actor.First);
        Assert.Null(Assert.Single(_model.Calls).Request.Screenshot);
        Assert.Contains("You may see the screen", _model.Calls[0].Conversation.Memory, StringComparison.Ordinal);

        var steps = (await service.GetStepsAsync(run.Id)).Select(s => (s.Kind, s.Text)).ToList();
        Assert.Equal((ClickyStepKind.Request, "Asked Claude"), steps[0]);
        Assert.Contains((ClickyStepKind.Action, "click (1, 2)"), steps);
        Assert.Contains((ClickyStepKind.Action, "click (10, 20)"), steps);
        Assert.Contains((ClickyStepKind.Action, "done: Changed the wallpaper"), steps);
        var pausedAt = steps.IndexOf((ClickyStepKind.Control, "Paused"));
        var resumedAt = steps.IndexOf((ClickyStepKind.Control, "Resumed"));
        Assert.True(pausedAt > steps.IndexOf((ClickyStepKind.Text, "Opening Settings.")), "paused after step 2");
        Assert.True(resumedAt > pausedAt);
        Assert.True(steps.IndexOf((ClickyStepKind.Text, "All done.")) > resumedAt, "step 3 after resume");
    }

    [Fact]
    public async Task Pause_holds_a_queued_run_before_it_starts()
    {
        var gate = new Gate();
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Queue"));
        _model.Then(Replies.Started(), gate, Replies.Completed()).Then(Replies.Text("Second."));

        var first = await service.SendAsync(clicky.Id, "One");
        await gate.Reached.WaitAsync(Timeout);
        var second = await service.SendAsync(clicky.Id, "Two");
        Assert.True(service.Pause(second.Id));
        gate.Open();
        await FinishedAsync(first.Id);

        await WaitForStatusAsync(service, second.Id, ClickyRunStatus.Paused);
        Assert.Single(_model.Calls);

        Assert.True(service.Resume(second.Id));
        var finished = await FinishedAsync(second.Id);
        Assert.Equal(ClickyRunStatus.Completed, finished.Outcome.Status);
        Assert.Equal(2, _model.Calls.Count);
    }

    [Fact]
    public async Task Cancel_while_paused_stops_the_run()
    {
        var gate = new Gate();
        var actor = new FakeClickyActor(new ActorStep("Step.", [new ClickAction(1, 1)], gate), new ActorStep("Never."));
        var service = CreateService(actor);
        var clicky = await service.CreateAsync(new ClickyDraft("Hands", UsesComputer: true));
        _model.Then(Replies.Started(), new ReplyText("Start."), Replies.Completed(ReplyStop.Actions));

        var run = await service.SendAsync(clicky.Id, "Go");
        await gate.Reached.WaitAsync(Timeout);
        service.Pause(run.Id);
        gate.Open();
        await WaitForStatusAsync(service, run.Id, ClickyRunStatus.Paused);

        Assert.True(service.Cancel(run.Id));
        var finished = await FinishedAsync(run.Id);

        Assert.Equal(ClickyRunStatus.Cancelled, finished.Outcome.Status);
        Assert.Equal("Start.\n\nStep.", finished.Outcome.Reply);
        Assert.Equal(1, actor.StepsStarted);
    }

    [Fact]
    public async Task Step_cap_stops_the_actor_and_is_not_a_success()
    {
        var actor = new FakeClickyActor([.. Enumerable.Range(1, 10).Select(i => new ActorStep($"Step {i}.", [new ClickAction(i, i)]))]);
        var service = CreateService(actor);
        var clicky = await service.CreateAsync(new ClickyDraft("Busy", UsesComputer: true, MaxSteps: 3));
        _model.Then(Replies.Started(), new ReplyText("Starting."), Replies.Completed(ReplyStop.Actions, null, new ClickAction(0, 0)));

        var outcome = await service.RunAsync(clicky.Id, new ClickyRunRequest("Do lots", ClickyRunTrigger.Chat));

        Assert.Equal(ClickyRunStatus.Completed, outcome.Status);
        Assert.False(outcome.Succeeded);
        Assert.Equal("Starting.\n\nStep 1.\n\nStep 2.", outcome.Reply);
        Assert.Equal(new ClickyActorOptions(2), actor.Options);
        Assert.Equal(2, actor.StepsStarted);
        Assert.Equal(3, (await service.GetRunAsync(outcome.RunId))!.Steps);
        Assert.Contains(await service.GetStepsAsync(outcome.RunId), s => s.Kind == ClickyStepKind.Control && s.Text == "Stopped after 3 steps (this Clicky's limit)");
    }

    // See the report: the cap is checked before asking the actor whether it has more, so a task
    // that finishes on exactly its last allowed step is reported as stopped at the limit.
    [Fact]
    public async Task Finishing_on_the_last_allowed_step_is_a_success()
    {
        var actor = new FakeClickyActor(
            new ActorStep("Step 1."),
            new ActorStep("Done.", [new DoneAction("All set")], Stop: ReplyStop.EndTurn));
        var service = CreateService(actor);
        var clicky = await service.CreateAsync(new ClickyDraft("Exact", UsesComputer: true, MaxSteps: 3));
        _model.Then(Replies.Started(), new ReplyText("Starting."), Replies.Completed(ReplyStop.Actions));

        var outcome = await service.RunAsync(clicky.Id, new ClickyRunRequest("Do three things", ClickyRunTrigger.Chat));

        Assert.True(outcome.Succeeded);
        Assert.Equal("All set", outcome.Summary);
        Assert.DoesNotContain(await service.GetStepsAsync(outcome.RunId), s => s.Kind == ClickyStepKind.Control);
    }

    [Fact]
    public async Task Actor_that_finishes_early_is_a_success()
    {
        var actor = new FakeClickyActor(new ActorStep("Done.", [new DoneAction("All set")], Stop: ReplyStop.EndTurn));
        var service = CreateService(actor);
        var clicky = await service.CreateAsync(new ClickyDraft("Quick hands", UsesComputer: true, MaxSteps: 5));
        _model.Then(Replies.Started(), new ReplyText("Starting."), Replies.Completed(ReplyStop.Actions));

        var outcome = await service.RunAsync(clicky.Id, new ClickyRunRequest("One thing", ClickyRunTrigger.Chat));

        Assert.True(outcome.Succeeded);
        Assert.Equal("All set", outcome.Summary);
        Assert.Equal(2, (await service.GetRunAsync(outcome.RunId))!.Steps);
    }

    [Fact]
    public async Task Delete_stops_an_active_run_and_removes_the_clicky()
    {
        var gate = new Gate();
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Doomed"));
        var folder = service.Workspace.EnsureCreated(clicky.Id);
        _model.Then(Replies.Started(), gate, Replies.Completed());

        var run = await service.SendAsync(clicky.Id, "Busy work");
        await gate.Reached.WaitAsync(Timeout);
        await service.DeleteAsync(clicky.Id);

        Assert.True(gate.Cancelled);
        Assert.Equal(ClickyRunStatus.Cancelled, (await FinishedAsync(run.Id)).Outcome.Status);
        Assert.Null(await service.GetAsync(clicky.Id));
        Assert.Empty(await service.ListAsync());
        Assert.Null(await service.GetRunAsync(run.Id));
        Assert.Empty(await service.GetMessagesAsync(clicky.Id));
        Assert.Empty(service.ActiveRuns);
        Assert.Contains(_changes, e => e.Change == ClickyChange.ClickyDeleted && e.ClickyId == clicky.Id);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task Retry_asks_the_same_thing_again()
    {
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Again"));
        _model.Then(Replies.Started(), new ClaudeBackendException(ClaudeFailure.Unavailable, "Overloaded.")).Then(Replies.Text("Worked this time."));
        await Assert.ThrowsAsync<ClaudeBackendException>(() => service.RunAsync(clicky.Id, new ClickyRunRequest("Check the build", RoutineId: "r9", RoutineName: "Nightly")));
        var failed = Assert.Single(await service.GetRunsAsync(clicky.Id));

        var retry = await service.RetryAsync(failed.Id);
        var finished = await FinishedAsync(retry.Id);

        Assert.NotEqual(failed.Id, retry.Id);
        Assert.Equal(ClickyRunTrigger.Routine, retry.Trigger);
        Assert.Equal("Check the build", retry.Prompt);
        Assert.Equal("Nightly", retry.RoutineName);
        Assert.Equal("r9", retry.RoutineId);
        Assert.True(finished.Outcome.Succeeded);
        Assert.Equal(2, (await service.GetRunsAsync(clicky.Id)).Count);
    }

    [Fact]
    public async Task Dispose_interrupts_running_runs_and_refuses_new_ones()
    {
        var gate = new Gate();
        var service = CreateService();
        var clicky = await service.CreateAsync(new ClickyDraft("Closing"));
        _model.Then(Replies.Started(), gate, Replies.Completed());

        var run = await service.SendAsync(clicky.Id, "Long job");
        await gate.Reached.WaitAsync(Timeout);
        await service.DisposeAsync();

        var finished = await FinishedAsync(run.Id);
        Assert.Equal(ClickyRunStatus.Interrupted, finished.Outcome.Status);
        var stored = await service.GetRunAsync(run.Id);
        Assert.Equal(ClickyRunStatus.Interrupted, stored!.Status);
        Assert.Equal("Clicky closed while this was running.", stored.Error);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.SendAsync(clicky.Id, "more"));
    }

    [Theory]
    [InlineData("**Sunny** and warm.\n\nMore.", "Sunny and warm.")]
    [InlineData("## Heading\nBody", "Heading")]
    [InlineData("\n\n  - First bullet\n- Second", "First bullet")]
    [InlineData("```html\n<p>x</p>\n```", "<p>x</p>")]
    [InlineData("> quoted", "quoted")]
    [InlineData("", "Done.")]
    [InlineData("```\n```", "Done.")]
    public void Summary_is_the_first_real_line(string reply, string summary)
    {
        Assert.Equal(summary, ClickyService.SummaryOf(reply));
    }

    [Fact]
    public void Summary_is_clipped()
    {
        var summary = ClickyService.SummaryOf(new string('w', 500));

        Assert.Equal(160, summary.Length);
        Assert.EndsWith("…", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Actions_are_described_for_the_activity_log()
    {
        Assert.Equal("click (10, 20) OK button", ClickyService.Describe(new ClickAction(10.4, 19.6, Target: " OK button ")));
        Assert.Equal("double-click (1, 2)", ClickyService.Describe(new ClickAction(1, 2, Count: 2)));
        Assert.Equal("right-click (1, 2)", ClickyService.Describe(new ClickAction(1, 2, MouseButton.Right)));
        Assert.Equal("type \"hello\"", ClickyService.Describe(new TypeAction("hello")));
        Assert.Equal("done", ClickyService.Describe(new DoneAction()));
        Assert.Equal("done: filed", ClickyService.Describe(new DoneAction("filed")));
        Assert.Equal("couldn't finish: no login", ClickyService.Describe(new DoneAction("no login", Success: false)));
        Assert.Equal("couldn't finish", ClickyService.Describe(new DoneAction(Success: false)));
        Assert.Equal("point at (3, 4) here", ClickyService.Describe(new PointAction(3, 4, "here")));
    }

    private static DateTimeOffset Ms(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());

    private static DateTimeOffset? Ms(DateTimeOffset? value) => value is { } v ? Ms(v) : null;

    private static ClickyAgent Stored(ClickyAgent clicky) =>
        clicky with { CreatedAt = Ms(clicky.CreatedAt), UpdatedAt = Ms(clicky.UpdatedAt), LastActivityAt = Ms(clicky.LastActivityAt) };

    private static ClickyRun Stored(ClickyRun run) =>
        run with { QueuedAt = Ms(run.QueuedAt), StartedAt = Ms(run.StartedAt), EndedAt = Ms(run.EndedAt), ScheduledFor = Ms(run.ScheduledFor) };

    private static async Task<ClickyRun> WaitForStatusAsync(ClickyService service, string runId, ClickyRunStatus status)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var run = await service.GetRunAsync(runId);
            if (run?.Status == status)
            {
                return run;
            }

            if (clock.Elapsed > Timeout)
            {
                Assert.Fail($"Run {runId} never reached {status}; it is {run?.Status}.");
            }

            await Task.Delay(10);
        }
    }

    private Task<ClickyRunFinishedEventArgs> FinishedAsync(string runId) =>
        Finish(runId).Task.WaitAsync(Timeout);

    private TaskCompletionSource<ClickyRunFinishedEventArgs> Finish(string runId) =>
        _finished.GetOrAdd(runId, _ => new TaskCompletionSource<ClickyRunFinishedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously));

    private ClickyService CreateService(IClickyActor? actor = null)
    {
        var service = new ClickyService(new ClickyPaths(_dir), _model, actor ?? new ChatOnlyClickyActor(), NullLogger<ClickyService>.Instance);
        service.Changed += (_, e) => _changes.Enqueue(e);
        service.RunFinished += (_, e) => Finish(e.Run.Id).TrySetResult(e);
        _services.Add(service);
        return service;
    }
}
