using Clicky.Core.Routines;
using static Clicky.Tests.Routines.RoutineScheduleTests;

namespace Clicky.Tests.Routines;

public class CronExpressionTests
{
    [Fact]
    public void Weekday_mornings()
    {
        var cron = CronExpression.Parse("0 8 * * 1-5");

        // Friday 2026-01-16 09:00 -> Monday 08:00.
        Assert.Equal(Utc(2026, 1, 19, 8, 0), cron.NextAfter(Utc(2026, 1, 16, 9, 0), TestZones.Utc));
    }

    [Fact]
    public void Steps_within_a_range()
    {
        var cron = CronExpression.Parse("*/15 9-17 * * *");

        Assert.Equal(Utc(2026, 1, 5, 9, 0), cron.NextAfter(Utc(2026, 1, 5, 8, 59), TestZones.Utc));
        Assert.Equal(Utc(2026, 1, 5, 9, 15), cron.NextAfter(Utc(2026, 1, 5, 9, 0), TestZones.Utc));
        Assert.Equal(Utc(2026, 1, 6, 9, 0), cron.NextAfter(Utc(2026, 1, 5, 17, 45), TestZones.Utc));
    }

    [Fact]
    public void Names_and_seven_as_sunday()
    {
        var byName = CronExpression.Parse("30 7 * JAN,FEB SUN");
        var bySeven = CronExpression.Parse("30 7 * 1-2 7");

        var after = Utc(2026, 1, 5, 0, 0);
        Assert.Equal(Utc(2026, 1, 11, 7, 30), byName.NextAfter(after, TestZones.Utc));
        Assert.Equal(byName.NextAfter(after, TestZones.Utc), bySeven.NextAfter(after, TestZones.Utc));
    }

    [Fact]
    public void Both_day_fields_restricted_match_either()
    {
        // The 1st of the month OR any Monday.
        var cron = CronExpression.Parse("0 9 1 * MON");

        // Thursday 2026-01-01 is the 1st; the next is Monday the 5th.
        Assert.Equal(Utc(2026, 1, 1, 9, 0), cron.NextAfter(Utc(2025, 12, 31, 12, 0), TestZones.Utc));
        Assert.Equal(Utc(2026, 1, 5, 9, 0), cron.NextAfter(Utc(2026, 1, 1, 9, 0), TestZones.Utc));
    }

    [Fact]
    public void Macros()
    {
        Assert.Equal(Utc(2026, 1, 2, 0, 0), CronExpression.Parse("@daily").NextAfter(Utc(2026, 1, 1, 0, 0), TestZones.Utc));
        Assert.Equal(Utc(2026, 1, 1, 1, 0), CronExpression.Parse("@hourly").NextAfter(Utc(2026, 1, 1, 0, 0), TestZones.Utc));
    }

    [Fact]
    public void Leap_day_waits_for_a_leap_year()
    {
        Assert.Equal(Utc(2028, 2, 29, 12, 0), CronExpression.Parse("0 12 29 2 *").NextAfter(Utc(2026, 1, 1, 0, 0), TestZones.Utc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("0 0 31 2 *")]
    [InlineData("*/0 * * * *")]
    [InlineData("0 8 * * FUNDAY")]
    [InlineData("5-1 * * * *")]
    public void Invalid_expressions_are_rejected_with_a_reason(string expression)
    {
        Assert.False(CronExpression.TryParse(expression, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
