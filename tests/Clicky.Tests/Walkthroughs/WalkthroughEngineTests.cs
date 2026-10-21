using Clicky.Core.Actions;
using Clicky.Core.Assistant;
using Clicky.Core.Buddy;
using Clicky.Core.Geometry;
using Clicky.Core.Skills;
using Clicky.Core.Walkthroughs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Clicky.Tests.Walkthroughs.WalkthroughTestKit;

namespace Clicky.Tests.Walkthroughs;

public sealed class WalkthroughEngineTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeStepWatcher _watcher = new();
    private readonly FakeRenderer _renderer = new();
    private readonly FakeTurns _turns = new();
    private readonly FakeForeground _foreground = new();
    private readonly BuddyPresence _presence = new(NullLogger<BuddyPresence>.Instance);
    private readonly System.Collections.Concurrent.ConcurrentQueue<WalkthroughSnapshot> _changes = new();
    private readonly WalkthroughEngine _engine;

    public WalkthroughEngineTests()
    {
        _engine = new WalkthroughEngine(
            _watcher,
            _renderer,
            _turns,
            _presence,
            _foreground,
            () => [Screen],
            NullLogger<WalkthroughEngine>.Instance,
            new WalkthroughOptions { IdleTimeout = TimeSpan.FromMinutes(5) },
            _time);
        _engine.Changed += (_, e) => _changes.Enqueue(e.Snapshot);
    }

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task A_step_in_a_reply_starts_a_walkthrough_and_waits_for_the_user()
    {
        Send(_engine, Shot, new SayAction("First, open the File menu."), Step(1, instruction: "Open the File menu"));
        await _engine.FlushAsync();

        var snapshot = Assert.IsType<WalkthroughSnapshot>(_engine.Current);
        Assert.Equal(WalkthroughPhase.Waiting, snapshot.Phase);
        Assert.Equal("Step 1 of 4", snapshot.Progress);
        Assert.Equal("Open the File menu", snapshot.Current?.Instruction);
        Assert.True(_watcher.IsWatching);
        var (action, options, _) = Assert.Single(_renderer.Shown);
        Assert.IsType<StepAction>(action);
        Assert.Equal(Timeout.InfiniteTimeSpan, options?.Hold);
    }

    [Fact]
    public async Task A_click_on_the_spot_settles_then_asks_for_the_next_step()
    {
        await StartAsync();

        _watcher.Report(new PointerClicked(new PixelPoint(505, 296), MouseButton.Left));
        await _engine.FlushAsync();
        Assert.Equal(WalkthroughPhase.Settling, _engine.Current?.Phase);
        Assert.Empty(_turns.All);

        var turn = await NextTurnAsync();
        Assert.Equal(WalkthroughTurnReason.StepDone, turn.Turn.Reason);
        Assert.Contains("step 1 (\"Do thing 1\")", turn.Turn.Prompt, StringComparison.Ordinal);
        Assert.Contains("give step 2", turn.Turn.Prompt, StringComparison.Ordinal);
        Assert.Equal(WalkthroughPhase.Thinking, _engine.Current?.Phase);

        var firstMark = _renderer.LastMark;
        turn.Reply(Shot, new SayAction("Now choose Export."), Step(2, 800, 400));
        await UntilAsync(() => _engine.Current?.Phase == WalkthroughPhase.Waiting);

        var snapshot = _engine.Current!;
        Assert.Equal(2, snapshot.Current?.Index);
        Assert.Equal(WalkthroughStepStatus.Done, snapshot.Steps[0].Status);
        Assert.True(firstMark.Dismissed);
        Assert.Equal(1, _watcher.Starts);
    }

    [Fact]
    public async Task Clicky_waits_while_the_user_keeps_clicking()
    {
        await StartAsync();
        _watcher.Report(new PointerClicked(new PixelPoint(500, 300), MouseButton.Left));
        await _engine.FlushAsync();

        _time.Advance(TimeSpan.FromMilliseconds(600));
        _watcher.Report(new PointerClicked(new PixelPoint(500, 300), MouseButton.Left)); // the second click of a double-click
        await _engine.FlushAsync();
        _time.Advance(TimeSpan.FromMilliseconds(150));
        await SettleThreadsAsync();
        Assert.Empty(_turns.All);

        _time.Advance(TimeSpan.FromMilliseconds(400));
        await UntilAsync(() => _turns.All.Count == 1);
    }

    [Fact]
    public async Task Done_from_Claude_finishes_and_removes_the_hooks()
    {
        await StartAsync();
        var turn = await DoStepAsync();
        var mark = _renderer.LastMark;

        turn.Reply(Shot, new SayAction("That's it, you're done."), new DoneAction("Exported"));
        var finished = await FinishedAsync();

        Assert.Equal(WalkthroughEnd.Completed, finished.End);
        Assert.Null(_engine.Current);
        Assert.False(_watcher.IsWatching);
        Assert.Equal(1, _watcher.Stops);
        Assert.True(mark.Dismissed);
    }

    [Fact]
    public async Task A_walkthrough_turn_without_a_step_finishes_it()
    {
        await StartAsync();
        var turn = await DoStepAsync();

        turn.Reply(Shot, new SayAction("All set."));
        Assert.Equal(WalkthroughEnd.Completed, (await FinishedAsync()).End);
    }

    [Fact]
    public async Task A_question_in_between_answered_without_a_step_keeps_the_walkthrough()
    {
        await StartAsync();

        Send(_engine, Shot, new SayAction("That's the ribbon."));
        await _engine.FlushAsync();

        Assert.Equal(WalkthroughPhase.Waiting, _engine.Current?.Phase);
        Assert.Equal(1, _engine.Current?.Current?.Index);
    }

    [Fact]
    public async Task Stray_clicks_lead_to_a_new_look_a_limited_number_of_times()
    {
        await StartAsync();

        for (var i = 0; i < 3; i++)
        {
            _watcher.Report(new PointerClicked(new PixelPoint(1500, 900), MouseButton.Left));
        }

        var turn = await NextTurnAsync();
        Assert.Equal(WalkthroughTurnReason.OffTrack, turn.Turn.Reason);
        Assert.Contains("clicked somewhere else", turn.Turn.Prompt, StringComparison.Ordinal);

        // Claude re-plans the same step; two more rounds of strays get one more look, then no more.
        turn.Reply(Shot, Step(1, 520, 310));
        await UntilAsync(() => _engine.Current?.Phase == WalkthroughPhase.Waiting);
        for (var i = 0; i < 3; i++)
        {
            _watcher.Report(new PointerClicked(new PixelPoint(1500, 900), MouseButton.Left));
        }

        var second = await NextTurnAsync(expected: 2);
        second.Reply(Shot, Step(1, 520, 310));
        await UntilAsync(() => _engine.Current?.Phase == WalkthroughPhase.Waiting);
        for (var i = 0; i < 3; i++)
        {
            _watcher.Report(new PointerClicked(new PixelPoint(1500, 900), MouseButton.Left));
        }

        await _engine.FlushAsync();
        Assert.Equal(WalkthroughPhase.Waiting, _engine.Current?.Phase);
        Assert.Equal(2, _turns.All.Count);
    }

    [Fact]
    public async Task Switching_to_another_app_is_off_track_but_the_shell_is_not()
    {
        await StartAsync();

        _watcher.Report(new ForegroundChanged(new ForegroundApp("explorer.exe", string.Empty)));
        _watcher.Report(new ForegroundChanged(new ForegroundApp("EXCEL.EXE", "Book2 - Excel")));
        _watcher.Report(new ForegroundChanged(new ForegroundApp("Clicky.exe", "Settings"), IsClicky: true));
        await _engine.FlushAsync();
        Assert.Equal(WalkthroughPhase.Waiting, _engine.Current?.Phase);

        _watcher.Report(new ForegroundChanged(new ForegroundApp("chrome.exe", "Inbox")));
        var turn = await NextTurnAsync();
        Assert.Equal(WalkthroughTurnReason.OffTrack, turn.Turn.Reason);
        Assert.Contains("switched to chrome (\"Inbox\")", turn.Turn.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keyboard_focus_on_the_target_counts_as_done()
    {
        await StartAsync();

        _watcher.Report(new ElementEvent(ElementEventKind.Focus, new PixelRect(0, 0, 1920, 1000), "Document")); // too big to be the target
        await _engine.FlushAsync();
        Assert.Equal(WalkthroughPhase.Waiting, _engine.Current?.Phase);

        _watcher.Report(new ElementEvent(ElementEventKind.Invoke, new PixelRect(470, 285, 540, 315), "Export"));
        var turn = await NextTurnAsync();
        Assert.Equal(WalkthroughTurnReason.StepDone, turn.Turn.Reason);
    }

    [Fact]
    public async Task Skip_and_done_from_the_card_ask_at_once()
    {
        Send(_engine, Shot, Step(1, x: null, y: null, instruction: "Press Ctrl+S"));
        await _engine.FlushAsync();
        Assert.False(_engine.Current?.Current?.HasTarget);

        // No spot on screen: clicks mean nothing, only the card moves it on.
        _watcher.Report(new PointerClicked(new PixelPoint(500, 300), MouseButton.Left));
        await _engine.FlushAsync();
        Assert.Equal(WalkthroughPhase.Waiting, _engine.Current?.Phase);

        _engine.MarkDone();
        var turn = await NextTurnAsync();
        Assert.Equal(WalkthroughTurnReason.StepDone, turn.Turn.Reason);

        turn.Reply(Shot, Step(2));
        await UntilAsync(() => _engine.Current?.Current?.Index == 2);
        _engine.Skip();
        var skipped = await NextTurnAsync(expected: 2);
        Assert.Equal(WalkthroughTurnReason.StepSkipped, skipped.Turn.Reason);
        Assert.Equal(WalkthroughStepStatus.Skipped, _engine.Current?.Steps[1].Status);
    }

    [Fact]
    public async Task Back_shows_the_previous_step_again()
    {
        await StartAsync();
        var turn = await DoStepAsync();
        turn.Reply(Shot, Step(2, 800, 400));
        await UntilAsync(() => _engine.Current?.Current?.Index == 2);
        Assert.True(_engine.Current?.CanGoBack);

        _engine.Back();
        await _engine.FlushAsync();

        var snapshot = _engine.Current!;
        Assert.Equal(1, snapshot.Current?.Index);
        Assert.Single(snapshot.Steps);
        var reshown = Assert.IsType<StepAction>(_renderer.Shown.Last().Action);
        Assert.Equal(1, reshown.Index);
    }

    [Fact]
    public async Task Cancel_ends_it_and_cancels_the_request_in_flight()
    {
        await StartAsync();
        var turn = await DoStepAsync();

        _engine.Cancel();
        var finished = await FinishedAsync();

        Assert.Equal(WalkthroughEnd.Cancelled, finished.End);
        Assert.True(turn.Token.IsCancellationRequested);

        // What the cancelled request still sends is ignored.
        turn.Scope.Observe(new ReplyAction(Step(2)), Shot);
        await _engine.FlushAsync();
        Assert.Null(_engine.Current);
    }

    [Fact]
    public async Task Talking_cancels_our_request_and_it_runs_again_after_the_answer()
    {
        await StartAsync();
        var turn = await DoStepAsync();

        _presence.SetState(BuddyState.Listening);
        await UntilAsync(() => turn.Token.IsCancellationRequested && _engine.Current?.Phase == WalkthroughPhase.Settling);

        // The user's question is answered (spoken), without a step.
        _presence.SetState(BuddyState.Thinking);
        Send(_engine, Shot, new SayAction("It's the blue one."));
        _presence.SetState(BuddyState.Speaking);
        await _engine.FlushAsync();
        _presence.SetState(BuddyState.Idle);
        await _engine.FlushAsync();
        Assert.Single(_turns.All);

        _time.Advance(new WalkthroughOptions().AfterQuestion);
        var again = await NextTurnAsync(expected: 2);
        Assert.Equal(WalkthroughTurnReason.StepDone, again.Turn.Reason);
        Assert.Equal(turn.Turn.Prompt, again.Turn.Prompt);
    }

    [Fact]
    public async Task A_failed_request_can_be_retried()
    {
        await StartAsync();
        var turn = await DoStepAsync();

        turn.Fail(new ClaudeBackendException(ClaudeFailure.Network, "Couldn't reach Claude."));
        await UntilAsync(() => _engine.Current?.Problem is not null);
        Assert.Equal(WalkthroughPhase.Waiting, _engine.Current?.Phase);
        Assert.Equal("Couldn't reach Claude.", _engine.Current?.Problem);

        _engine.Retry();
        var again = await NextTurnAsync(expected: 2);
        Assert.Equal(turn.Turn.Prompt, again.Turn.Prompt);
        Assert.Null(_engine.Current?.Problem);
    }

    [Fact]
    public async Task The_fifteenth_step_wraps_up()
    {
        Send(_engine, Shot, Step(15, total: 15));
        await _engine.FlushAsync();

        _watcher.Report(new PointerClicked(new PixelPoint(500, 300), MouseButton.Left));
        var turn = await NextTurnAsync();
        Assert.Equal(WalkthroughTurnReason.LimitReached, turn.Turn.Reason);
        Assert.Contains("Don't give another step", turn.Turn.Prompt, StringComparison.Ordinal);

        turn.Reply(Shot, new SayAction("What's left is saving."), new DoneAction());
        Assert.Equal(WalkthroughEnd.LimitReached, (await FinishedAsync()).End);
    }

    [Fact]
    public async Task A_new_step_one_from_the_user_replaces_the_walkthrough()
    {
        await StartAsync();
        var turn = await DoStepAsync();
        turn.Reply(Shot, Step(2));
        await UntilAsync(() => _engine.Current?.Current?.Index == 2);
        var first = _engine.Current!.Id;

        Send(_engine, Shot, Step(1, instruction: "Open Settings"));
        await _engine.FlushAsync();

        Assert.Equal(WalkthroughEnd.Replaced, _changes.Single(c => c.Id == first && c.End is not null).End);
        Assert.NotEqual(first, _engine.Current?.Id);
        Assert.Equal("Open Settings", _engine.Current?.Current?.Instruction);
        Assert.Equal(2, _watcher.Starts);
    }

    [Fact]
    public async Task No_progress_for_a_long_time_ends_it()
    {
        await StartAsync();

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(WalkthroughEnd.TimedOut, (await FinishedAsync()).End);
        Assert.False(_watcher.IsWatching);
    }

    [Fact]
    public async Task Extra_steps_in_one_reply_are_ignored()
    {
        Send(_engine, Shot, Step(1), Step(2), Step(3));
        await _engine.FlushAsync();

        Assert.Single(_engine.Current!.Steps);
        Assert.Single(_renderer.Shown);
    }

    [Fact]
    public async Task A_talk_loop_that_only_passes_the_completed_reply_still_works()
    {
        _engine.Observe(new ReplyCompleted(ReplyStop.EndTurn, [new SayAction("Click File."), Step(1)], ReplyUsage.None, null, TimeSpan.Zero, "test"), Shot);
        await _engine.FlushAsync();

        Assert.Equal(1, _engine.Current?.Current?.Index);
    }

    private async Task StartAsync()
    {
        Send(_engine, Shot, new SayAction("First, click here."), Step(1));
        await _engine.FlushAsync();
        Assert.Equal(WalkthroughPhase.Waiting, _engine.Current?.Phase);
    }

    private async Task<PendingTurn> DoStepAsync()
    {
        _watcher.Report(new PointerClicked(new PixelPoint(500, 300), MouseButton.Left));
        return await NextTurnAsync();
    }

    /// <summary>Lets the settle delays run out and waits for turn number <paramref name="expected"/>.</summary>
    private async Task<PendingTurn> NextTurnAsync(int expected = 1)
    {
        await _engine.FlushAsync();
        for (var i = 0; i < 200 && _turns.All.Count < expected; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            await SettleThreadsAsync();
        }

        Assert.Equal(expected, _turns.All.Count);
        await _engine.FlushAsync();
        return _turns.Last;
    }

    private async Task<WalkthroughSnapshot> FinishedAsync()
    {
        await UntilAsync(() => _changes.Any(c => c.Phase == WalkthroughPhase.Finished));
        return _changes.Last(c => c.Phase == WalkthroughPhase.Finished);
    }

    private async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await SettleThreadsAsync();
        }

        Assert.True(condition());
    }

    private async Task SettleThreadsAsync()
    {
        await Task.Delay(5);
        await _engine.FlushAsync();
    }
}
