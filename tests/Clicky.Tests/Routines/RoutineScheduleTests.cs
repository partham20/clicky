using System.Globalization;
using Clicky.Core.Routines;

namespace Clicky.Tests.Routines;

public class RoutineScheduleTests
{
    private static readonly DateTimeOffset Anchor = Utc(2026, 1, 1, 0, 0);

    [Fact]
    public void Daily_runs_today_if_the_time_is_still_ahead_otherwise_tomorrow()
    {
        var schedule = RoutineSchedule.Daily(new TimeOnly(8, 0));

        Assert.Equal(Utc(2026, 1, 14, 8, 0), schedule.NextAfter(Utc(2026, 1, 14, 7, 59), TestZones.Utc, Anchor));
        Assert.Equal(Utc(2026, 1, 15, 8, 0), schedule.NextAfter(Utc(2026, 1, 14, 8, 0), TestZones.Utc, Anchor));
    }

    [Fact]
    public void Weekdays_skip_the_weekend()
    {
        // 2026-01-16 is a Friday.
        var schedule = RoutineSchedule.Weekdays(new TimeOnly(8, 0));

        Assert.Equal(Utc(2026, 1, 19, 8, 0), schedule.NextAfter(Utc(2026, 1, 16, 9, 0), TestZones.Utc, Anchor));
    }

    [Fact]
    public void Daily_follows_local_time_across_a_dst_change()
    {
        var schedule = RoutineSchedule.Daily(new TimeOnly(8, 0));

        // Winter: 08:00 local is 08:00 UTC. Summer: 08:00 local is 07:00 UTC.
        Assert.Equal(Utc(2026, 3, 28, 8, 0), schedule.NextAfter(Utc(2026, 3, 28, 0, 0), TestZones.London, Anchor));
        Assert.Equal(Utc(2026, 3, 29, 7, 0), schedule.NextAfter(Utc(2026, 3, 28, 8, 0), TestZones.London, Anchor));
    }

    [Fact]
    public void A_time_skipped_by_dst_runs_at_the_first_valid_minute_after()
    {
        // 2026-03-29 01:00 local jumps to 02:00; 01:30 doesn't exist.
        var schedule = RoutineSchedule.Daily(new TimeOnly(1, 30));

        Assert.Equal(Utc(2026, 3, 29, 1, 0), schedule.NextAfter(Utc(2026, 3, 28, 12, 0), TestZones.London, Anchor));
    }

    [Fact]
    public void A_time_repeated_by_dst_runs_once()
    {
        // 2026-10-25 02:00 BST falls back to 01:00 GMT; 01:30 happens twice.
        var schedule = RoutineSchedule.Daily(new TimeOnly(1, 30));

        var first = schedule.NextAfter(Utc(2026, 10, 24, 12, 0), TestZones.London, Anchor);
        Assert.Equal(Utc(2026, 10, 25, 0, 30), first);
        Assert.Equal(Utc(2026, 10, 26, 1, 30), schedule.NextAfter(first!.Value, TestZones.London, Anchor));
    }

    [Fact]
    public void Interval_without_a_window_counts_from_the_anchor()
    {
        var schedule = RoutineSchedule.Every(TimeSpan.FromHours(3));
        var anchor = Utc(2026, 1, 1, 10, 17);

        Assert.Equal(Utc(2026, 1, 1, 13, 17), schedule.NextAfter(anchor, TestZones.Utc, anchor));
        Assert.Equal(Utc(2026, 1, 2, 1, 17), schedule.NextAfter(Utc(2026, 1, 1, 23, 0), TestZones.Utc, anchor));
    }

    [Fact]
    public void Interval_in_a_window_starts_each_day_at_from_and_stops_before_until()
    {
        var schedule = RoutineSchedule.Every(TimeSpan.FromHours(3), new TimeOnly(9, 0), new TimeOnly(18, 0));

        Assert.Equal(Utc(2026, 1, 5, 9, 0), schedule.NextAfter(Utc(2026, 1, 5, 6, 0), TestZones.Utc, Anchor));
        Assert.Equal(Utc(2026, 1, 5, 15, 0), schedule.NextAfter(Utc(2026, 1, 5, 12, 0), TestZones.Utc, Anchor));

        // 18:00 is excluded, so after 15:00 the next run is tomorrow at 9.
        Assert.Equal(Utc(2026, 1, 6, 9, 0), schedule.NextAfter(Utc(2026, 1, 5, 15, 0), TestZones.Utc, Anchor));
    }

    [Fact]
    public void Interval_limited_to_weekdays_skips_the_weekend()
    {
        var schedule = RoutineSchedule.Every(TimeSpan.FromHours(1), days: RoutineDays.Weekdays);

        // Friday 23:30 -> Monday 00:00.
        Assert.Equal(Utc(2026, 1, 19, 0, 0), schedule.NextAfter(Utc(2026, 1, 16, 23, 30), TestZones.Utc, Anchor));
    }

    [Fact]
    public void Monthly_on_the_31st_runs_on_the_last_day_of_short_months()
    {
        var schedule = RoutineSchedule.Monthly(31, new TimeOnly(9, 0));

        Assert.Equal(Utc(2026, 2, 28, 9, 0), schedule.NextAfter(Utc(2026, 2, 1, 0, 0), TestZones.Utc, Anchor));
        Assert.Equal(Utc(2026, 3, 31, 9, 0), schedule.NextAfter(Utc(2026, 2, 28, 9, 0), TestZones.Utc, Anchor));
    }

    [Fact]
    public void Once_runs_once_and_then_never()
    {
        var schedule = RoutineSchedule.Once(new DateTime(2026, 2, 1, 14, 30, 0));

        Assert.Equal(Utc(2026, 2, 1, 14, 30), schedule.NextAfter(Utc(2026, 1, 1, 0, 0), TestZones.Utc, Anchor));
        Assert.Null(schedule.NextAfter(Utc(2026, 2, 1, 14, 30), TestZones.Utc, Anchor));
    }

    [Fact]
    public void At_startup_has_no_clock_time()
    {
        Assert.Null(RoutineSchedule.AtStartup().NextAfter(Anchor, TestZones.Utc, Anchor));
    }

    [Fact]
    public void Latest_due_finds_the_most_recent_missed_run()
    {
        var daily = RoutineSchedule.Daily(new TimeOnly(8, 0));
        Assert.Equal(Utc(2026, 1, 20, 8, 0), daily.LatestDue(Utc(2026, 1, 17, 8, 0), Utc(2026, 1, 20, 9, 0), TestZones.Utc, Anchor));

        var hourly = RoutineSchedule.Every(TimeSpan.FromHours(1));
        Assert.Equal(Utc(2026, 1, 20, 9, 0), hourly.LatestDue(Utc(2026, 1, 3, 1, 0), Utc(2026, 1, 20, 9, 59), TestZones.Utc, Anchor));

        // Nothing later has passed: the due run itself.
        Assert.Equal(Utc(2026, 1, 20, 8, 0), daily.LatestDue(Utc(2026, 1, 20, 8, 0), Utc(2026, 1, 20, 9, 0), TestZones.Utc, Anchor));
    }

    [Theory]
    [InlineData(4, "Routines can run at most every 5 minutes.")]
    [InlineData(10081, "For gaps longer than a week, use a daily or monthly routine.")]
    public void Interval_limits_are_enforced(int minutes, string error)
    {
        Assert.Equal(error, RoutineSchedule.Every(TimeSpan.FromMinutes(minutes)).Validate());
    }

    [Fact]
    public void Invalid_schedules_say_why()
    {
        Assert.NotNull(RoutineSchedule.Daily(new TimeOnly(8, 0), RoutineDays.None).Validate());
        Assert.NotNull(RoutineSchedule.Every(TimeSpan.FromHours(1), new TimeOnly(18, 0), new TimeOnly(9, 0)).Validate());
        Assert.NotNull(RoutineSchedule.Monthly(32, new TimeOnly(8, 0)).Validate());
        Assert.NotNull(RoutineSchedule.FromCron("not cron").Validate());
        Assert.Null(RoutineSchedule.FromCron("0 8 * * 1-5").Validate());
    }

    [Fact]
    public void Describe_reads_like_a_person_wrote_it()
    {
        // ICU's en-US time pattern has a narrow no-break space before AM; pin the pattern.
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.DateTimeFormat.ShortTimePattern = "h:mm tt";

        Assert.Equal("Every weekday at 8:00 AM", RoutineSchedule.Weekdays(new TimeOnly(8, 0)).Describe(culture));
        Assert.Equal("Every day at 6:30 PM", RoutineSchedule.Daily(new TimeOnly(18, 30)).Describe(culture));
        Assert.Equal("On Mon, Wed at 9:00 AM", RoutineSchedule.Daily(new TimeOnly(9, 0), RoutineDays.Monday | RoutineDays.Wednesday).Describe(culture));
        Assert.Equal("Every 3 hours, 9:00 AM–6:00 PM", RoutineSchedule.Every(TimeSpan.FromHours(3), new TimeOnly(9, 0), new TimeOnly(18, 0)).Describe(culture));
        Assert.Equal("Every 1 h 30 min", RoutineSchedule.Every(TimeSpan.FromMinutes(90)).Describe(culture));
        Assert.Equal("Monthly on the last day at 9:00 AM", RoutineSchedule.Monthly(31, new TimeOnly(9, 0)).Describe(culture));
        Assert.Equal("Monthly on the 2nd at 9:00 AM", RoutineSchedule.Monthly(2, new TimeOnly(9, 0)).Describe(culture));
        Assert.Equal("When Clicky starts", RoutineSchedule.AtStartup().Describe(culture));
    }

    internal static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}

/// <summary>Time zones built in code, so tests behave the same on Windows and Linux.</summary>
internal static class TestZones
{
    public static TimeZoneInfo Utc { get; } = TimeZoneInfo.CreateCustomTimeZone("Test UTC", TimeSpan.Zero, "Test UTC", "Test UTC");

    /// <summary>UK rules: GMT, BST from the last Sunday of March 01:00 to the last Sunday of October 02:00 local.</summary>
    public static TimeZoneInfo London { get; } = TimeZoneInfo.CreateCustomTimeZone(
        "Test London",
        TimeSpan.Zero,
        "Test London",
        "GMT",
        "BST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);
}
