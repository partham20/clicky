using Clicky.Core.Assistant;
using Clicky.Core.Routines;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using static Clicky.Tests.Routines.RoutineScheduleTests;

namespace Clicky.Tests.Routines;

public class RoutineEngineTests
{
    // Monday 2026-01-05 07:00 UTC.
    private static readonly DateTimeOffset Monday7 = Utc(2026, 1, 5, 7, 0);

    private static RoutineDraft Briefing(RoutineSchedule? schedule = null) =>
        new("Morning briefing", "Summarize my calendar and the news.", schedule ?? RoutineSchedule.Daily(new TimeOnly(8, 0)));

    [Fact]
    public async Task Runs_on_time_and_schedules_the_next_one()
    {
        await using var h = new EngineHarness(Monday7);
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());
        Assert.Equal(Utc(2026, 1, 5, 8, 0), routine.NextDueAt);
        Assert.Equal("Next: today", h.Engine.GetStatus(routine.Id).Text[..11]);

        await h.AdvanceAsync(TimeSpan.FromHours(1));
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).LastStatus == RoutineRunStatus.Succeeded, "the run to finish");

        var call = Assert.Single(h.Runner.Calls);
        Assert.Equal(RoutineTrigger.Schedule, call.Trigger);
        Assert.False(call.IsCatchUp);
        Assert.Equal(Utc(2026, 1, 5, 8, 0), call.ScheduledFor);
        Assert.Equal("Summarize my calendar and the news.", call.Prompt);

        var after = h.Get(routine.Id);
        Assert.Equal(Utc(2026, 1, 6, 8, 0), after.NextDueAt);
        Assert.Equal("All done.", after.LastMessage);

        var run = Assert.Single(await h.Engine.ListRunsAsync(routine.Id));
        Assert.Equal(RoutineRunStatus.Succeeded, run.Status);
        Assert.Equal("All done.\nFull text.", run.Output);
    }

    [Fact]
    public async Task Catches_up_once_after_a_long_sleep()
    {
        await using var h = new EngineHarness(Monday7);
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());

        // Asleep from Monday 07:00 until Thursday 09:00: four 8:00 runs missed. It waits for the
        // PC to settle even without a wake-up notice (the overdue timer gives it away).
        await h.SleepAsync(TimeSpan.FromDays(3) + TimeSpan.FromHours(2));
        Assert.Empty(h.Runner.Calls);
        await h.AdvanceAsync(RoutineEngine.ResumeSettle);
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).LastStatus == RoutineRunStatus.Succeeded, "the catch-up run");
        await EngineHarness.Settle();

        var call = Assert.Single(h.Runner.Calls);
        Assert.Equal(RoutineTrigger.CatchUp, call.Trigger);
        Assert.True(call.IsCatchUp);
        Assert.Equal(Utc(2026, 1, 8, 8, 0), call.ScheduledFor);
        Assert.Equal(Utc(2026, 1, 9, 8, 0), h.Get(routine.Id).NextDueAt);
    }

    [Fact]
    public async Task A_run_missed_outside_the_catch_up_window_is_skipped_and_recorded()
    {
        await using var h = new EngineHarness(Monday7, new RoutineSettings(CatchUpHours: 12));
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());

        // Wakes Monday 22:00: 14 hours after the 8:00 run.
        await h.SleepAsync(TimeSpan.FromHours(15));

        Assert.Empty(h.Runner.Calls);
        var after = h.Get(routine.Id);
        Assert.Equal(RoutineRunStatus.Skipped, after.LastStatus);
        Assert.Equal(Utc(2026, 1, 6, 8, 0), after.NextDueAt);
        Assert.Equal(RoutineRunStatus.Skipped, Assert.Single(await h.Engine.ListRunsAsync(routine.Id)).Status);
    }

    [Fact]
    public async Task Survives_an_offline_hour_by_waiting_for_the_network()
    {
        await using var h = new EngineHarness(Monday7);
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());
        h.Signals.GoOnline(false);

        await h.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Empty(h.Runner.Calls);
        Assert.Equal(new RoutineStatus(RoutineState.Waiting, "Waiting for the network"), h.Engine.GetStatus(routine.Id));

        await h.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Empty(h.Runner.Calls);

        h.Signals.GoOnline(true);
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).LastStatus == RoutineRunStatus.Succeeded, "the run after the network came back");

        var call = Assert.Single(h.Runner.Calls);
        Assert.Equal(RoutineTrigger.CatchUp, call.Trigger);
        Assert.Equal(Utc(2026, 1, 5, 8, 0), call.ScheduledFor);
    }

    [Fact]
    public async Task Waits_for_the_pc_to_settle_after_waking()
    {
        await using var h = new EngineHarness(Monday7);
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());

        h.Time.Advance(TimeSpan.FromHours(1));
        h.Signals.Resume();
        await EngineHarness.Settle();
        Assert.Empty(h.Runner.Calls);
        Assert.Equal(RoutineState.Waiting, h.Engine.GetStatus(routine.Id).State);

        await h.AdvanceAsync(RoutineEngine.ResumeSettle);
        await EngineHarness.WaitUntil(() => h.Runner.Calls.Count == 1, "the run after settling");
    }

    [Fact]
    public async Task Pauses_after_three_failures_in_a_row_with_the_reason()
    {
        await using var h = new EngineHarness(Monday7);
        h.Runner.Handler = (_, _) => throw new ClaudeBackendException(ClaudeFailure.Unauthorized, "Your API key was rejected.");
        await h.StartAsync();
        RoutinePausedEventArgs? paused = null;
        h.Engine.Paused += (_, e) => paused = e;
        var routine = await h.Engine.CreateAsync(Briefing(RoutineSchedule.Every(TimeSpan.FromHours(1))));

        for (var i = 1; i <= 3; i++)
        {
            await h.AdvanceAsync(TimeSpan.FromHours(1));
            var expected = i;
            await EngineHarness.WaitUntil(() => h.Get(routine.Id).ConsecutiveFailures == expected || !h.Get(routine.Id).Enabled, $"failure {i}");
        }

        var after = h.Get(routine.Id);
        Assert.False(after.Enabled);
        Assert.Equal("Paused after 3 failed runs in a row. Last error: Your API key was rejected.", after.PausedReason);
        Assert.Equal(RoutineState.Paused, h.Engine.GetStatus(routine.Id).State);
        await EngineHarness.WaitUntil(() => paused is not null, "the Paused event");
        Assert.Equal(routine.Id, paused!.Routine.Id);

        await h.AdvanceAsync(TimeSpan.FromHours(3));
        Assert.Equal(3, h.Runner.Calls.Count);

        // Resuming clears the count and the reason.
        var resumed = await h.Engine.SetEnabledAsync(routine.Id, true);
        Assert.Null(resumed.PausedReason);
        Assert.Equal(0, resumed.ConsecutiveFailures);
        Assert.Equal(h.Now + TimeSpan.FromHours(1), resumed.NextDueAt);
    }

    [Fact]
    public async Task A_success_resets_the_failure_count()
    {
        await using var h = new EngineHarness(Monday7);
        var fail = true;
        h.Runner.Handler = (_, _) => Task.FromResult(fail ? RoutineRunResult.Failure("Couldn't open the site.") : RoutineRunResult.Success("ok"));
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing(RoutineSchedule.Every(TimeSpan.FromHours(1))));

        await h.AdvanceAsync(TimeSpan.FromHours(1));
        await h.AdvanceAsync(TimeSpan.FromHours(1));
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).ConsecutiveFailures == 2, "two failures");
        Assert.Equal("Couldn't open the site.", h.Get(routine.Id).LastMessage);

        fail = false;
        await h.AdvanceAsync(TimeSpan.FromHours(1));
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).LastStatus == RoutineRunStatus.Succeeded, "a success");
        Assert.Equal(0, h.Get(routine.Id).ConsecutiveFailures);
        Assert.True(h.Get(routine.Id).Enabled);
    }

    [Fact]
    public async Task Losing_the_network_mid_run_is_retried_not_counted()
    {
        await using var h = new EngineHarness(Monday7);
        var attempts = 0;
        h.Runner.Handler = (_, _) =>
        {
            if (++attempts == 1)
            {
                h.Signals.IsOnline = false;
                throw new ClaudeBackendException(ClaudeFailure.Network, "Couldn't reach Claude.");
            }

            return Task.FromResult(RoutineRunResult.Success("ok"));
        };
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());

        await h.AdvanceAsync(TimeSpan.FromHours(1));
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).LastStatus == RoutineRunStatus.Interrupted, "the interrupted run");
        Assert.Equal(0, h.Get(routine.Id).ConsecutiveFailures);
        Assert.NotNull(h.Get(routine.Id).WaitingSince);

        h.Signals.GoOnline(true);
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).LastStatus == RoutineRunStatus.Succeeded, "the retry");
        Assert.Equal(2, h.Runner.Calls.Count);
        Assert.Equal(Utc(2026, 1, 6, 8, 0), h.Get(routine.Id).NextDueAt);
    }

    [Fact]
    public async Task Sleep_interrupts_a_run_and_it_runs_again_after_waking()
    {
        await using var h = new EngineHarness(Monday7);
        var attempts = 0;
        h.Runner.Handler = async (_, ct) =>
        {
            if (++attempts == 1)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return RoutineRunResult.Success("ok");
        };
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());

        await h.AdvanceAsync(TimeSpan.FromHours(1));
        await EngineHarness.WaitUntil(() => h.Engine.GetStatus(routine.Id).State == RoutineState.Running, "the run to start");

        h.Signals.Suspend();
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).LastStatus == RoutineRunStatus.Interrupted, "the run to stop for sleep");
        Assert.Equal(0, h.Get(routine.Id).ConsecutiveFailures);

        h.Time.Advance(TimeSpan.FromHours(1));
        h.Signals.Resume();
        await h.AdvanceAsync(RoutineEngine.ResumeSettle);
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).LastStatus == RoutineRunStatus.Succeeded, "the rerun after waking");
        Assert.Equal(2, h.Runner.Calls.Count);
    }

    [Fact]
    public async Task Holds_during_a_call_then_runs()
    {
        await using var h = new EngineHarness(Monday7);
        h.Quiet.Reasons = QuietReasons.InCall;
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());

        await h.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Empty(h.Runner.Calls);
        Assert.Equal("Waiting: You seem to be in a call (Teams).", h.Engine.GetStatus(routine.Id).Text);

        h.Quiet.Reasons = QuietReasons.None;
        await h.AdvanceAsync(RoutineEngine.HoldRecheck);
        await EngineHarness.WaitUntil(() => h.Runner.Calls.Count == 1, "the run after the call");
    }

    [Fact]
    public async Task Focus_assist_alone_does_not_hold_a_run()
    {
        await using var h = new EngineHarness(Monday7);
        h.Quiet.Reasons = QuietReasons.FocusAssist | QuietReasons.QuietMode;
        await h.StartAsync();
        await h.Engine.CreateAsync(Briefing());

        await h.AdvanceAsync(TimeSpan.FromHours(1));
        await EngineHarness.WaitUntil(() => h.Runner.Calls.Count == 1, "the run");
    }

    [Fact]
    public async Task Only_max_concurrent_runs_run_at_once()
    {
        await using var h = new EngineHarness(Monday7);
        var release = new TaskCompletionSource<RoutineRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Runner.Handler = (_, _) => release.Task;
        await h.StartAsync();
        var first = await h.Engine.CreateAsync(Briefing());
        var second = await h.Engine.CreateAsync(Briefing() with { Name = "Inbox check" });

        await h.AdvanceAsync(TimeSpan.FromHours(1));
        await EngineHarness.WaitUntil(() => h.Runner.Calls.Count == 1, "the first run");
        await EngineHarness.Settle();
        Assert.Single(h.Runner.Calls);
        Assert.Equal("Waiting for another routine to finish", h.Engine.GetStatus(second.Id).Text);

        release.SetResult(RoutineRunResult.Success("ok"));
        await EngineHarness.WaitUntil(() => h.Runner.Calls.Count == 2, "the second run");
        Assert.Equal([first.Id, second.Id], h.Runner.Calls.Select(c => c.RoutineId));
    }

    [Fact]
    public async Task Run_now_ignores_the_network_and_keeps_the_schedule()
    {
        await using var h = new EngineHarness(Monday7);
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());
        h.Signals.IsOnline = false;

        await h.Engine.RunNowAsync(routine.Id);
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).LastStatus == RoutineRunStatus.Succeeded, "the manual run");

        Assert.Equal(RoutineTrigger.Manual, Assert.Single(h.Runner.Calls).Trigger);
        Assert.Equal(Utc(2026, 1, 5, 8, 0), h.Get(routine.Id).NextDueAt);
    }

    [Fact]
    public async Task Stop_cancels_a_run_without_counting_a_failure()
    {
        await using var h = new EngineHarness(Monday7);
        h.Runner.Handler = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return RoutineRunResult.Success();
        };
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());

        await h.Engine.RunNowAsync(routine.Id);
        await EngineHarness.WaitUntil(() => h.Engine.GetStatus(routine.Id).State == RoutineState.Running, "the run to start");
        Assert.True(await h.Engine.StopRunAsync(routine.Id));
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).LastStatus == RoutineRunStatus.Cancelled, "the run to stop");
        Assert.Equal(0, h.Get(routine.Id).ConsecutiveFailures);
    }

    [Fact]
    public async Task A_run_stopped_where_it_ran_is_recorded_as_stopped()
    {
        await using var h = new EngineHarness(Monday7);
        h.Runner.Handler = (_, _) => Task.FromResult(RoutineRunResult.Stopped());
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());

        await h.AdvanceAsync(TimeSpan.FromHours(1));
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).LastStatus == RoutineRunStatus.Cancelled, "the stopped run");
        Assert.Equal(0, h.Get(routine.Id).ConsecutiveFailures);
        Assert.Equal(Utc(2026, 1, 6, 8, 0), h.Get(routine.Id).NextDueAt);
    }

    [Fact]
    public async Task A_run_that_takes_too_long_is_stopped_and_counts_as_a_failure()
    {
        await using var h = new EngineHarness(Monday7, new RoutineSettings(RunTimeoutMinutes: 5));
        h.Runner.Handler = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return RoutineRunResult.Success();
        };
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());

        await h.Engine.RunNowAsync(routine.Id);
        await EngineHarness.WaitUntil(() => h.Engine.GetStatus(routine.Id).State == RoutineState.Running, "the run to start");
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).LastStatus == RoutineRunStatus.Failed, "the timeout");
        Assert.Equal("Took longer than 5 minutes, so it was stopped.", h.Get(routine.Id).LastMessage);
        Assert.Equal(1, h.Get(routine.Id).ConsecutiveFailures);
    }

    [Fact]
    public async Task A_run_accepted_while_offline_still_happens_after_a_restart()
    {
        await using var h = new EngineHarness(Monday7);
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());
        h.Signals.GoOnline(false);
        await h.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.NotNull(h.Get(routine.Id).WaitingSince);

        h.Signals.IsOnline = true;
        await h.RestartAsync(TimeSpan.FromMinutes(10));
        await EngineHarness.WaitUntil(() => h.Runner.Calls.Count == 1, "the run after restart");
        Assert.Equal(Utc(2026, 1, 5, 8, 0), h.Runner.Calls.Single().ScheduledFor);
    }

    [Fact]
    public async Task A_run_missed_while_clicky_was_closed_is_caught_up_on_start()
    {
        await using var h = new EngineHarness(Monday7);
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());

        // Closed 07:00-10:00.
        await h.RestartAsync(TimeSpan.FromHours(3));
        await EngineHarness.WaitUntil(() => h.Runner.Calls.Count == 1, "the catch-up run on start");
        Assert.True(h.Runner.Calls.Single().IsCatchUp);
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).LastStatus == RoutineRunStatus.Succeeded, "it to finish");
    }

    [Fact]
    public async Task Startup_routines_run_shortly_after_clicky_starts()
    {
        await using var h = new EngineHarness(Monday7);
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing(RoutineSchedule.AtStartup()));
        Assert.Equal("Next: when Clicky starts", h.Engine.GetStatus(routine.Id).Text);

        await h.RestartAsync(TimeSpan.FromHours(1));
        Assert.Empty(h.Runner.Calls);
        await h.AdvanceAsync(RoutineEngine.StartupDelay);
        await EngineHarness.WaitUntil(() => h.Runner.Calls.Count == 1, "the startup run");
        Assert.Equal(RoutineTrigger.Startup, h.Runner.Calls.Single().Trigger);
    }

    [Fact]
    public async Task A_once_routine_runs_and_is_then_finished()
    {
        await using var h = new EngineHarness(Monday7);
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing(RoutineSchedule.Once(new DateTime(2026, 1, 5, 9, 0, 0))));

        await h.AdvanceAsync(TimeSpan.FromHours(2));
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).LastStatus == RoutineRunStatus.Succeeded, "the run");
        Assert.True(h.Get(routine.Id).IsFinished);
        Assert.Equal(RoutineState.Finished, h.Engine.GetStatus(routine.Id).State);
    }

    [Fact]
    public async Task Turning_routines_off_stops_scheduled_runs_but_not_run_now()
    {
        await using var h = new EngineHarness(Monday7, new RoutineSettings(Enabled: false));
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());
        Assert.Equal(RoutineState.Off, h.Engine.GetStatus(routine.Id).State);

        await h.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Empty(h.Runner.Calls);

        await h.Engine.RunNowAsync(routine.Id);
        await EngineHarness.WaitUntil(() => h.Runner.Calls.Count == 1, "the manual run");
    }

    [Fact]
    public async Task Editing_the_schedule_starts_counting_from_now()
    {
        await using var h = new EngineHarness(Monday7);
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());

        var edited = await h.Engine.UpdateAsync(routine.Id, Briefing(RoutineSchedule.Every(TimeSpan.FromMinutes(30))) with { Name = "  Renamed  " });
        Assert.Equal("Renamed", edited.Name);
        Assert.Equal(h.Now + TimeSpan.FromMinutes(30), edited.NextDueAt);

        // A name-only edit keeps the schedule.
        var renamed = await h.Engine.UpdateAsync(routine.Id, Briefing(RoutineSchedule.Every(TimeSpan.FromMinutes(30))) with { Name = "Again" });
        Assert.Equal(edited.NextDueAt, renamed.NextDueAt);
    }

    [Fact]
    public async Task Invalid_drafts_are_refused_with_a_message()
    {
        await using var h = new EngineHarness(Monday7);
        await h.StartAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => h.Engine.CreateAsync(Briefing(RoutineSchedule.Every(TimeSpan.FromMinutes(1)))));
        Assert.Equal("Routines can run at most every 5 minutes.", ex.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => h.Engine.CreateAsync(new RoutineDraft(" ", "x", RoutineSchedule.AtStartup())));
    }

    [Fact]
    public async Task Deleting_a_running_routine_stops_it_and_removes_its_history()
    {
        await using var h = new EngineHarness(Monday7);
        h.Runner.Handler = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return RoutineRunResult.Success();
        };
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());
        await h.Engine.RunNowAsync(routine.Id);
        await EngineHarness.WaitUntil(() => h.Engine.GetStatus(routine.Id).State == RoutineState.Running, "the run to start");

        Assert.True(await h.Engine.DeleteAsync(routine.Id));
        Assert.Empty(h.Engine.Routines);
        await EngineHarness.Settle();
        Assert.Empty(await h.Store.ListRunsAsync(routine.Id));
    }

    [Fact]
    public async Task A_time_zone_change_moves_upcoming_runs_to_the_new_local_time()
    {
        await using var h = new EngineHarness(Monday7);
        await h.StartAsync();
        var routine = await h.Engine.CreateAsync(Briefing());
        Assert.Equal(Utc(2026, 1, 5, 8, 0), routine.NextDueAt);

        // Flew two hours east: 8:00 local is now 06:00 UTC, which has passed, so tomorrow's.
        h.Zone = TimeZoneInfo.CreateCustomTimeZone("Test +2", TimeSpan.FromHours(2), "Test +2", "Test +2");
        h.Signals.ChangeTime();
        await EngineHarness.WaitUntil(() => h.Get(routine.Id).NextDueAt == Utc(2026, 1, 6, 6, 0), "the reschedule");
        Assert.Empty(h.Runner.Calls);
    }
}
