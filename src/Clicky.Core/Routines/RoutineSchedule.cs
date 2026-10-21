using System.Globalization;
using System.Text;

namespace Clicky.Core.Routines;

/// <summary>How a routine repeats. Written to routines.db as JSON; do not rename.</summary>
public enum RoutineScheduleKind
{
    /// <summary>One time, at <see cref="RoutineSchedule.OnceAt"/>.</summary>
    Once,

    /// <summary>At <see cref="RoutineSchedule.At"/> on the days in <see cref="RoutineSchedule.Days"/> (every day by default).</summary>
    Daily,

    /// <summary>Every <see cref="RoutineSchedule.IntervalMinutes"/>, optionally only between From and Until on some days.</summary>
    Interval,

    /// <summary>At <see cref="RoutineSchedule.At"/> on <see cref="RoutineSchedule.DayOfMonth"/> (the last day in shorter months).</summary>
    Monthly,

    /// <summary>A five-field cron expression in local time (see <see cref="CronExpression"/>).</summary>
    Cron,

    /// <summary>Each time Clicky starts (after Windows sign-in when Clicky starts with Windows).</summary>
    AtStartup,
}

/// <summary>Days of the week as flags. Written to routines.db; do not renumber.</summary>
[Flags]
public enum RoutineDays
{
    None = 0,
    Sunday = 1 << 0,
    Monday = 1 << 1,
    Tuesday = 1 << 2,
    Wednesday = 1 << 3,
    Thursday = 1 << 4,
    Friday = 1 << 5,
    Saturday = 1 << 6,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    Every = Weekdays | Weekend,
}

/// <summary>
/// When a routine runs, in local wall-clock time so "8:00 every morning" stays 8:00 across DST and
/// time-zone changes. Immutable; build one with the factory methods.
/// </summary>
/// <remarks>
/// DST: a time that doesn't exist (the hour skipped in spring) runs at the first valid minute after
/// it; a time that happens twice (the hour repeated in autumn) runs once, the first time.
/// </remarks>
public sealed record RoutineSchedule
{
    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 7 * 24 * 60;

    public RoutineScheduleKind Kind { get; init; }

    /// <summary>Time of day for Daily and Monthly.</summary>
    public TimeOnly At { get; init; }

    /// <summary>Days a Daily or Interval schedule runs on.</summary>
    public RoutineDays Days { get; init; } = RoutineDays.Every;

    /// <summary>Local date and time of a Once schedule (DateTimeKind.Unspecified).</summary>
    public DateTime? OnceAt { get; init; }

    public int IntervalMinutes { get; init; }

    /// <summary>Interval: first run of each active day. With <see cref="Until"/> null, runs go on to midnight.</summary>
    public TimeOnly? From { get; init; }

    /// <summary>Interval: no runs at or after this time of day.</summary>
    public TimeOnly? Until { get; init; }

    /// <summary>Monthly: 1-31; 31 means the last day of every month.</summary>
    public int DayOfMonth { get; init; }

    public string? Cron { get; init; }

    public static RoutineSchedule Once(DateTime localTime) =>
        new() { Kind = RoutineScheduleKind.Once, OnceAt = DateTime.SpecifyKind(TruncateToMinute(localTime), DateTimeKind.Unspecified) };

    public static RoutineSchedule Daily(TimeOnly at, RoutineDays days = RoutineDays.Every) =>
        new() { Kind = RoutineScheduleKind.Daily, At = TruncateToMinute(at), Days = days };

    public static RoutineSchedule Weekdays(TimeOnly at) => Daily(at, RoutineDays.Weekdays);

    public static RoutineSchedule Every(TimeSpan interval, TimeOnly? from = null, TimeOnly? until = null, RoutineDays days = RoutineDays.Every) =>
        new()
        {
            Kind = RoutineScheduleKind.Interval,
            IntervalMinutes = (int)Math.Round(interval.TotalMinutes),
            From = from is { } f ? TruncateToMinute(f) : null,
            Until = until is { } u ? TruncateToMinute(u) : null,
            Days = days,
        };

    public static RoutineSchedule Monthly(int dayOfMonth, TimeOnly at) =>
        new() { Kind = RoutineScheduleKind.Monthly, DayOfMonth = dayOfMonth, At = TruncateToMinute(at) };

    public static RoutineSchedule FromCron(string expression) => new() { Kind = RoutineScheduleKind.Cron, Cron = expression.Trim() };

    public static RoutineSchedule AtStartup() => new() { Kind = RoutineScheduleKind.AtStartup };

    /// <summary>True when the schedule repeats along a fixed grid from the routine's anchor instead of from times of day.</summary>
    private bool IsAnchored => Kind == RoutineScheduleKind.Interval && From is null && Until is null && (Days & RoutineDays.Every) == RoutineDays.Every;

    /// <summary>What's wrong with this schedule, in a sentence for the user; null if it is fine.</summary>
    public string? Validate()
    {
        switch (Kind)
        {
            case RoutineScheduleKind.Once:
                return OnceAt is null ? "Pick a date and time." : null;
            case RoutineScheduleKind.Daily:
                return (Days & RoutineDays.Every) == RoutineDays.None ? "Pick at least one day." : null;
            case RoutineScheduleKind.Interval:
                if (IntervalMinutes < MinIntervalMinutes)
                {
                    return $"Routines can run at most every {MinIntervalMinutes} minutes.";
                }

                if (IntervalMinutes > MaxIntervalMinutes)
                {
                    return "For gaps longer than a week, use a daily or monthly routine.";
                }

                if ((Days & RoutineDays.Every) == RoutineDays.None)
                {
                    return "Pick at least one day.";
                }

                if (Until is { } until && until <= (From ?? TimeOnly.MinValue))
                {
                    return "The end time must be later than the start time.";
                }

                return null;
            case RoutineScheduleKind.Monthly:
                return DayOfMonth is < 1 or > 31 ? "Pick a day of the month from 1 to 31." : null;
            case RoutineScheduleKind.Cron:
                return CronExpression.TryParse(Cron, out _, out var error) ? null : error;
            case RoutineScheduleKind.AtStartup:
                return null;
            default:
                return "Unknown schedule.";
        }
    }

    /// <summary>
    /// The first run strictly after <paramref name="after"/>; null if there is none (a Once in the
    /// past, AtStartup, or an invalid schedule).
    /// </summary>
    /// <param name="anchor">Where an interval without a time window counts from (the routine's creation).</param>
    public DateTimeOffset? NextAfter(DateTimeOffset after, TimeZoneInfo zone, DateTimeOffset anchor)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (Validate() is not null)
        {
            return null;
        }

        return Kind switch
        {
            RoutineScheduleKind.Once => LocalTime.ToInstant(OnceAt!.Value, zone) is var once && once > after ? once : null,
            RoutineScheduleKind.Daily => NextDaily(after, zone),
            RoutineScheduleKind.Interval when IsAnchored => NextAnchored(after, anchor),
            RoutineScheduleKind.Interval => NextInWindow(after, zone),
            RoutineScheduleKind.Monthly => NextMonthly(after, zone),
            RoutineScheduleKind.Cron => CronExpression.Parse(Cron!).NextAfter(after, zone),
            _ => null,
        };
    }

    /// <summary>
    /// The latest run at or before <paramref name="now"/>, starting from a run known to be due
    /// (<paramref name="due"/>, which is returned if no later one has passed). Used to catch up
    /// once instead of once per missed run.
    /// </summary>
    public DateTimeOffset LatestDue(DateTimeOffset due, DateTimeOffset now, TimeZoneInfo zone, DateTimeOffset anchor)
    {
        if (IsAnchored && now > due)
        {
            var step = TimeSpan.FromMinutes(IntervalMinutes);
            var steps = (now - anchor).Ticks / step.Ticks;
            var latest = anchor + TimeSpan.FromTicks(steps * step.Ticks);
            return latest > due ? latest : due;
        }

        // Bounded: a week of 5-minute runs is about 2,000 steps, and anything older is outside the
        // catch-up window anyway.
        var current = due;
        for (var i = 0; i < 5000; i++)
        {
            if (NextAfter(current, zone, anchor) is not { } next || next > now)
            {
                return current;
            }

            current = next;
        }

        return current;
    }

    /// <summary>"Every weekday at 8:00 AM", for lists and the assistant.</summary>
    public string Describe(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string Time(TimeOnly t) => t.ToString("t", culture);

        switch (Kind)
        {
            case RoutineScheduleKind.Once:
                return OnceAt is { } once
                    ? $"Once, {once.ToString("ddd d MMM yyyy", culture)} at {Time(TimeOnly.FromDateTime(once))}"
                    : "Once";
            case RoutineScheduleKind.Daily:
                return $"{DescribeDays(Days, "Every day")} at {Time(At)}";
            case RoutineScheduleKind.Interval:
                var text = new StringBuilder("Every ").Append(DescribeInterval(IntervalMinutes));
                if (From is not null || Until is not null)
                {
                    text.Append(", ").Append(Time(From ?? TimeOnly.MinValue)).Append('–').Append(Until is { } u ? Time(u) : "midnight");
                }

                if ((Days & RoutineDays.Every) != RoutineDays.Every)
                {
                    text.Append(", ").Append(DescribeDays(Days, string.Empty).ToLower(culture));
                }

                return text.ToString();
            case RoutineScheduleKind.Monthly:
                return DayOfMonth >= 31
                    ? $"Monthly on the last day at {Time(At)}"
                    : $"Monthly on the {Ordinal(DayOfMonth)} at {Time(At)}";
            case RoutineScheduleKind.Cron:
                return $"Cron: {Cron}";
            case RoutineScheduleKind.AtStartup:
                return "When Clicky starts";
            default:
                return Kind.ToString();
        }
    }

    internal static string DescribeInterval(int minutes) => minutes switch
    {
        _ when minutes % 1440 == 0 => minutes == 1440 ? "day" : $"{minutes / 1440} days",
        _ when minutes % 60 == 0 => minutes == 60 ? "hour" : $"{minutes / 60} hours",
        _ when minutes > 60 => $"{minutes / 60} h {minutes % 60} min",
        _ => $"{minutes} minutes",
    };

    private static string DescribeDays(RoutineDays days, string every)
    {
        days &= RoutineDays.Every;
        return days switch
        {
            RoutineDays.Every => every,
            RoutineDays.Weekdays => "Every weekday",
            RoutineDays.Weekend => "Weekends",
            _ => "On " + string.Join(", ", Enum.GetValues<DayOfWeek>().Where(d => days.HasFlag(Flag(d))).Select(d => d.ToString()[..3])),
        };
    }

    private static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}th",
        _ => (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" },
    };

    internal static RoutineDays Flag(DayOfWeek day) => (RoutineDays)(1 << (int)day);

    private DateTimeOffset? NextDaily(DateTimeOffset after, TimeZoneInfo zone)
    {
        // Start a day early: the local date of `after` may be ahead of a run still due "yesterday" in a DST edge.
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime).AddDays(-1);
        for (var i = 0; i < 9; i++, date = date.AddDays(1))
        {
            if (!Days.HasFlag(Flag(date.DayOfWeek)))
            {
                continue;
            }

            var run = LocalTime.ToInstant(date.ToDateTime(At), zone);
            if (run > after)
            {
                return run;
            }
        }

        return null;
    }

    private DateTimeOffset NextAnchored(DateTimeOffset after, DateTimeOffset anchor)
    {
        if (after < anchor)
        {
            return anchor;
        }

        var step = TimeSpan.FromMinutes(IntervalMinutes);
        var steps = ((after - anchor).Ticks / step.Ticks) + 1;
        return anchor + TimeSpan.FromTicks(steps * step.Ticks);
    }

    private DateTimeOffset? NextInWindow(DateTimeOffset after, TimeZoneInfo zone)
    {
        var from = From ?? TimeOnly.MinValue;
        var step = TimeSpan.FromMinutes(IntervalMinutes);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime).AddDays(-1);
        for (var i = 0; i < 9; i++, date = date.AddDays(1))
        {
            if (!Days.HasFlag(Flag(date.DayOfWeek)))
            {
                continue;
            }

            var dayStart = date.ToDateTime(TimeOnly.MinValue);
            var end = Until is { } until ? date.ToDateTime(until) : dayStart.AddDays(1);
            for (var local = date.ToDateTime(from); local < end; local += step)
            {
                var run = LocalTime.ToInstant(local, zone);
                if (run > after)
                {
                    return run;
                }
            }
        }

        return null;
    }

    private DateTimeOffset? NextMonthly(DateTimeOffset after, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var month = new DateOnly(local.Year, local.Month, 1).AddMonths(-1);
        for (var i = 0; i < 4; i++, month = month.AddMonths(1))
        {
            var day = Math.Min(DayOfMonth, DateTime.DaysInMonth(month.Year, month.Month));
            var run = LocalTime.ToInstant(new DateOnly(month.Year, month.Month, day).ToDateTime(At), zone);
            if (run > after)
            {
                return run;
            }
        }

        return null;
    }

    private static DateTime TruncateToMinute(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Kind);

    private static TimeOnly TruncateToMinute(TimeOnly t) => new(t.Hour, t.Minute);
}

/// <summary>Local wall-clock time to an instant, with the DST rules every schedule shares.</summary>
internal static class LocalTime
{
    /// <summary>
    /// The instant <paramref name="local"/> happens in <paramref name="zone"/>. A skipped time moves
    /// to the first valid minute after it; a repeated time takes its first occurrence.
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var i = 0; i < 24 * 60 && zone.IsInvalidTime(local); i++)
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
