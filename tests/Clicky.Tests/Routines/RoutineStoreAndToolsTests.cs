using System.Text.Json;
using Clicky.Core.Routines;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using static Clicky.Tests.Routines.RoutineScheduleTests;

namespace Clicky.Tests.Routines;

public sealed class RoutineStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "clicky-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private RoutineStore Open() => new(Path.Combine(_directory, "routines.db"), NullLogger<RoutineStore>.Instance);

    private static Routine Sample(string id = "r1") => new()
    {
        Id = id,
        Name = "Inbox check",
        Prompt = "Check my inbox.\nFlag anything urgent.",
        ClickyId = "c42",
        Schedule = RoutineSchedule.Every(TimeSpan.FromHours(2), new TimeOnly(9, 0), new TimeOnly(17, 0), RoutineDays.Weekdays),
        Enabled = false,
        PausedReason = "Paused after 3 failed runs in a row.",
        ConsecutiveFailures = 3,
        CreatedAt = Utc(2026, 1, 1, 9, 0),
        UpdatedAt = Utc(2026, 1, 2, 9, 0),
        AnchorAt = Utc(2026, 1, 1, 9, 30),
        NextDueAt = Utc(2026, 1, 3, 9, 0),
        WaitingSince = Utc(2026, 1, 3, 9, 1),
        LastRunAt = Utc(2026, 1, 2, 11, 0),
        LastStatus = RoutineRunStatus.Failed,
        LastMessage = "Couldn't reach the mail server.",
    };

    [Fact]
    public async Task Routines_round_trip_every_field()
    {
        await using var store = Open();
        var routine = Sample();
        await store.SaveAsync(routine);

        Assert.Equal(routine, Assert.Single(await store.ListAsync()));
    }

    [Fact]
    public async Task Saving_again_updates_in_place()
    {
        await using var store = Open();
        await store.SaveAsync(Sample());
        await store.SaveAsync(Sample() with { Name = "Renamed", Schedule = RoutineSchedule.FromCron("0 8 * * 1-5"), NextDueAt = null, ClickyId = null });

        var stored = Assert.Single(await store.ListAsync());
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal("0 8 * * 1-5", stored.Schedule.Cron);
        Assert.Null(stored.NextDueAt);
        Assert.Null(stored.ClickyId);
    }

    [Fact]
    public async Task Survives_reopening_the_file()
    {
        await using (var first = Open())
        {
            await first.SaveAsync(Sample());
        }

        await using var second = Open();
        Assert.Equal(Sample(), Assert.Single(await second.ListAsync()));
    }

    [Fact]
    public async Task History_is_newest_first_pruned_and_deleted_with_the_routine()
    {
        await using var store = Open();
        await store.SaveAsync(Sample());
        long last = 0;
        for (var i = 0; i < RoutineStore.KeepRunsPerRoutine + 5; i++)
        {
            var at = Utc(2026, 1, 1, 9, 0).AddHours(i);
            last = await store.AddRunAsync(new RoutineRun(0, "r1", at, at, null, RoutineRunStatus.Running, RoutineTrigger.Schedule, null, null));
            await store.FinishRunAsync(last, "r1", RoutineRunStatus.Succeeded, at.AddMinutes(1), $"run {i}", new string('x', RoutineStore.MaxOutputChars + 10));
        }

        var runs = await store.ListRunsAsync("r1");
        Assert.Equal(RoutineStore.KeepRunsPerRoutine, runs.Count);
        Assert.Equal(last, runs[0].Id);
        Assert.Equal($"run {RoutineStore.KeepRunsPerRoutine + 4}", runs[0].Message);
        Assert.Equal(RoutineRunStatus.Succeeded, runs[0].Status);
        Assert.True(runs[0].Output!.Length <= RoutineStore.MaxOutputChars + 2);

        Assert.True(await store.DeleteAsync("r1"));
        Assert.Empty(await store.ListRunsAsync("r1"));
        Assert.False(await store.DeleteAsync("r1"));
    }

    [Fact]
    public async Task Runs_left_running_by_a_crash_are_closed_as_interrupted()
    {
        await using var store = Open();
        await store.SaveAsync(Sample());
        var at = Utc(2026, 1, 1, 9, 0);
        await store.AddRunAsync(new RoutineRun(0, "r1", at, at, null, RoutineRunStatus.Running, RoutineTrigger.Manual, null, null));

        Assert.Equal(1, await store.CloseAbandonedRunsAsync(at.AddHours(1)));
        var run = Assert.Single(await store.ListRunsAsync("r1"));
        Assert.Equal(RoutineRunStatus.Interrupted, run.Status);
        Assert.Equal(at.AddHours(1), run.FinishedAt);
    }
}

public class RoutineToolsTests
{
    private static readonly DateTimeOffset Monday7 = Utc(2026, 1, 5, 7, 0);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Definitions_are_valid_tools()
    {
        Assert.Equal(["create_routine", "list_routines", "pause_routine", "run_routine_now", "delete_routine"], RoutineTools.Definitions.Select(t => t.Name));
        Assert.All(RoutineTools.Definitions, t => Assert.Equal("object", t.InputSchema.GetProperty("type").GetString()));
        Assert.True(RoutineTools.Definitions.Single(t => t.Name == RoutineTools.Delete).NeedsConfirmation);
        Assert.True(RoutineTools.IsRoutineTool("create_routine"));
        Assert.False(RoutineTools.IsRoutineTool("point"));
    }

    [Theory]
    [InlineData("""{ "kind": "daily", "time": "08:00" }""", "Daily", null)]
    [InlineData("""{ "kind": "weekdays", "time": "7:30" }""", "Daily", null)]
    [InlineData("""{ "kind": "weekly", "time": "09:00", "days": ["mon", "thu"] }""", "Daily", null)]
    [InlineData("""{ "kind": "every", "every_minutes": 180, "from": "09:00", "until": "21:00" }""", "Interval", null)]
    [InlineData("""{ "kind": "monthly", "day_of_month": 1, "time": "10:00" }""", "Monthly", null)]
    [InlineData("""{ "kind": "once", "date": "2026-02-01", "time": "14:30" }""", "Once", null)]
    [InlineData("""{ "kind": "cron", "cron": "0 8 * * 1-5" }""", "Cron", null)]
    [InlineData("""{ "kind": "startup" }""", "AtStartup", null)]
    [InlineData("""{ "kind": "daily" }""", null, "time is required (HH:mm).")]
    [InlineData("""{ "kind": "daily", "time": "8am" }""", null, "time must be HH:mm, 24-hour: \"8am\".")]
    [InlineData("""{ "kind": "weekly", "time": "09:00" }""", null, "weekly needs days.")]
    [InlineData("""{ "kind": "every", "every_minutes": 2 }""", null, "Routines can run at most every 5 minutes.")]
    [InlineData("""{ "kind": "hourly" }""", null, "schedule.kind must be once, daily, weekdays, weekly, every, monthly, cron or startup.")]
    public void Parses_schedules_or_says_what_is_wrong(string json, string? kind, string? error)
    {
        if (error is null)
        {
            Assert.Equal(kind, RoutineTools.ParseSchedule(Json(json)).Kind.ToString());
        }
        else
        {
            Assert.Equal(error, Assert.Throws<ArgumentException>(() => RoutineTools.ParseSchedule(Json(json))).Message);
        }
    }

    [Fact]
    public void Weekly_days_become_flags()
    {
        var schedule = RoutineTools.ParseSchedule(Json("""{ "kind": "weekly", "time": "09:00", "days": ["mon", "thu"] }"""));
        Assert.Equal(RoutineDays.Monday | RoutineDays.Thursday, schedule.Days);
        Assert.Equal(new TimeOnly(9, 0), schedule.At);
    }

    [Fact]
    public async Task Create_list_pause_run_and_delete_by_conversation()
    {
        await using var h = new EngineHarness(Monday7);
        await h.StartAsync();
        var tools = new RoutineTools(h.Engine, h.Time, () => TestZones.Utc);

        var created = await tools.CallAsync(RoutineTools.Create, Json("""
            { "name": "Morning briefing", "prompt": "Brief me on my calendar.", "schedule": { "kind": "weekdays", "time": "08:00" } }
            """), clickyId: "pdf-desk");
        Assert.False(created.IsError, created.Text);
        Assert.StartsWith("Created routine \"Morning briefing\"", created.Text, StringComparison.Ordinal);
        Assert.Contains("Next run: today 08:00", created.Text, StringComparison.Ordinal);
        var routine = Assert.Single(h.Engine.Routines);
        Assert.Equal("pdf-desk", routine.ClickyId);
        Assert.Equal(RoutineDays.Weekdays, routine.Schedule.Days);

        var list = await tools.CallAsync(RoutineTools.List, Json("{}"));
        Assert.Contains("\"Morning briefing\"", list.Text, StringComparison.Ordinal);
        Assert.Contains("Does: Brief me on my calendar.", list.Text, StringComparison.Ordinal);

        var paused = await tools.CallAsync(RoutineTools.Pause, Json("""{ "routine": "morning briefing", "paused": true }"""));
        Assert.False(paused.IsError, paused.Text);
        Assert.False(h.Get(routine.Id).Enabled);

        var ran = await tools.CallAsync(RoutineTools.RunNow, Json($$"""{ "routine": "{{routine.Id}}" }"""));
        Assert.False(ran.IsError, ran.Text);
        await EngineHarness.WaitUntil(() => h.Runner.Calls.Count == 1, "the run");

        var deleted = await tools.CallAsync(RoutineTools.Delete, Json("""{ "routine": "Morning briefing" }"""));
        Assert.False(deleted.IsError, deleted.Text);
        Assert.Empty(h.Engine.Routines);
    }

    [Fact]
    public async Task Problems_come_back_as_errors_for_claude()
    {
        await using var h = new EngineHarness(Monday7);
        await h.StartAsync();
        var tools = new RoutineTools(h.Engine, h.Time, () => TestZones.Utc);

        Assert.True((await tools.CallAsync(RoutineTools.Create, Json("""{ "name": "x", "schedule": { "kind": "startup" } }"""))).IsError);
        Assert.Equal(
            "That time has already passed.",
            (await tools.CallAsync(RoutineTools.Create, Json("""{ "name": "x", "prompt": "y", "schedule": { "kind": "once", "date": "2025-01-01", "time": "08:00" } }"""))).Text);
        Assert.StartsWith("No routine called", (await tools.CallAsync(RoutineTools.Pause, Json("""{ "routine": "nope", "paused": true }"""))).Text, StringComparison.Ordinal);
        Assert.True((await tools.CallAsync("bogus", Json("{}"))).IsError);
        Assert.Equal("No routines yet.", (await tools.CallAsync(RoutineTools.List, Json("{}"))).Text);
    }
}

public class RoutineMiscTests
{
    [Fact]
    public void The_fallback_runner_tells_claude_nobody_is_watching_and_when_it_is_late()
    {
        var request = new RoutineRunRequest("r", "Morning briefing", "Brief me.", null, Utc(2026, 1, 5, 8, 0), IsCatchUp: true, RoutineTrigger.CatchUp);

        var text = AssistantClickyRunner.Compose(request, TestZones.Utc);

        Assert.StartsWith("[Routine \"Morning briefing\", scheduled for Monday 5 January 2026, 08:00. It is running late", text, StringComparison.Ordinal);
        Assert.Contains("Nobody is waiting at the screen", text, StringComparison.Ordinal);
        Assert.EndsWith("]\nBrief me.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Routine_settings_default_and_clamp()
    {
        var defaults = ClickySettings.Default.Normalize().Routines;
        Assert.True(defaults.Enabled);
        Assert.Equal(12, defaults.CatchUpHours);
        Assert.Equal(1, defaults.MaxConcurrentRuns);
        Assert.True(defaults.HoldDuringCalls);

        var clamped = new RoutineSettings(CatchUpHours: -1, MaxConcurrentRuns: 9, RunTimeoutMinutes: 0).Normalize();
        Assert.Equal(0, clamped.CatchUpHours);
        Assert.Equal(RoutineSettings.MaxMaxConcurrentRuns, clamped.MaxConcurrentRuns);
        Assert.Equal(1, clamped.RunTimeoutMinutes);

        Assert.NotNull((ClickySettings.Default with { Routines = null! }).Normalize().Routines);
    }

    [Fact]
    public void Times_read_naturally()
    {
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.GetCultureInfo("en-US").Clone();
        culture.DateTimeFormat.ShortTimePattern = "h:mm tt";
        var now = Utc(2026, 1, 5, 7, 0);

        Assert.Equal("today 8:00 AM", RoutineFormat.When(Utc(2026, 1, 5, 8, 0), now, TestZones.Utc, culture));
        Assert.Equal("tomorrow 8:00 AM", RoutineFormat.When(Utc(2026, 1, 6, 8, 0), now, TestZones.Utc, culture));
        Assert.Equal("Fri 8:00 AM", RoutineFormat.When(Utc(2026, 1, 9, 8, 0), now, TestZones.Utc, culture));
        Assert.Equal("20 Jan 8:00 AM", RoutineFormat.When(Utc(2026, 1, 20, 8, 0), now, TestZones.Utc, culture));
    }
}
