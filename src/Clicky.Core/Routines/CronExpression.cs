using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Clicky.Core.Routines;

/// <summary>
/// Standard five-field cron (minute hour day-of-month month day-of-week) in local time, for
/// schedules the simple kinds can't express. Supports *, lists, ranges, steps (*/15, 8-18/2),
/// month and day names (JAN, MON), 7 as Sunday, and @hourly/@daily/@weekly/@monthly/@yearly.
/// As in Vixie cron, when both day fields are restricted a day matching either one runs.
/// </summary>
public sealed class CronExpression
{
    private static readonly string[] MonthNames = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly ulong _minutes;
    private readonly uint _hours;
    private readonly uint _daysOfMonth;
    private readonly ushort _months;
    private readonly byte _daysOfWeek;
    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;

    private CronExpression(ulong minutes, uint hours, uint daysOfMonth, ushort months, byte daysOfWeek, bool domRestricted, bool dowRestricted)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _domRestricted = domRestricted;
        _dowRestricted = dowRestricted;
    }

    /// <exception cref="FormatException">Not a valid expression; the message says why.</exception>
    public static CronExpression Parse(string expression) =>
        TryParse(expression, out var cron, out var error) ? cron : throw new FormatException(error);

    public static bool TryParse(string? expression, [NotNullWhen(true)] out CronExpression? cron, [NotNullWhen(false)] out string? error)
    {
        cron = null;
        var text = (expression ?? string.Empty).Trim();
        text = text.ToUpperInvariant() switch
        {
            "@HOURLY" => "0 * * * *",
            "@DAILY" or "@MIDNIGHT" => "0 0 * * *",
            "@WEEKLY" => "0 0 * * 0",
            "@MONTHLY" => "0 0 1 * *",
            "@YEARLY" or "@ANNUALLY" => "0 0 1 1 *",
            _ => text,
        };

        var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = "A cron expression has five fields: minute hour day-of-month month day-of-week.";
            return false;
        }

        if (!TryField(fields[0], 0, 59, null, "minute", out var minutes, out error) ||
            !TryField(fields[1], 0, 23, null, "hour", out var hours, out error) ||
            !TryField(fields[2], 1, 31, null, "day of month", out var dom, out error) ||
            !TryField(fields[3], 1, 12, MonthNames, "month", out var months, out error) ||
            !TryField(fields[4], 0, 7, DayNames, "day of week", out var dow, out error))
        {
            return false;
        }

        // 7 is Sunday too.
        if ((dow & (1UL << 7)) != 0)
        {
            dow = (dow | 1UL) & 0x7F;
        }

        if (!CanEverMatch(dom, months))
        {
            error = "That day never happens in the months given.";
            return false;
        }

        cron = new CronExpression(minutes, (uint)hours, (uint)dom, (ushort)months, (byte)dow, fields[2] != "*" && fields[2] != "?", fields[4] != "*" && fields[4] != "?");
        error = null;
        return true;
    }

    /// <summary>The first run strictly after <paramref name="after"/>, or null if none within four years.</summary>
    public DateTimeOffset? NextAfter(DateTimeOffset after, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var t = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var limit = t.AddYears(4);

        while (t < limit)
        {
            if ((_months & (1 << t.Month)) == 0)
            {
                t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
                continue;
            }

            if (!DayMatches(t))
            {
                t = t.Date.AddDays(1);
                continue;
            }

            if ((_hours & (1u << t.Hour)) == 0)
            {
                t = t.Date.AddHours(t.Hour + 1);
                continue;
            }

            if ((_minutes & (1UL << t.Minute)) == 0)
            {
                t = t.AddMinutes(1);
                continue;
            }

            var run = LocalTime.ToInstant(t, zone);
            if (run > after)
            {
                return run;
            }

            // The repeated autumn hour: this wall time already ran.
            t = t.AddMinutes(1);
        }

        return null;
    }

    private bool DayMatches(DateTime t)
    {
        var dom = (_daysOfMonth & (1u << t.Day)) != 0;
        var dow = (_daysOfWeek & (1 << (int)t.DayOfWeek)) != 0;
        return _domRestricted && _dowRestricted ? dom || dow : dom && dow;
    }

    private static bool CanEverMatch(ulong dom, ulong months)
    {
        int[] longest = [0, 31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
        for (var m = 1; m <= 12; m++)
        {
            if ((months & (1UL << m)) == 0)
            {
                continue;
            }

            for (var d = 1; d <= longest[m]; d++)
            {
                if ((dom & (1UL << d)) != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryField(string field, int min, int max, string[]? names, string label, out ulong bits, [NotNullWhen(false)] out string? error)
    {
        bits = 0;
        error = null;
        foreach (var part in field.Split(','))
        {
            var step = 1;
            var range = part;
            var slash = part.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                {
                    error = $"Bad step in the {label} field: \"{part}\".";
                    return false;
                }

                range = part[..slash];
            }

            int low, high;
            if (range is "*" or "?")
            {
                (low, high) = (min, max);
            }
            else
            {
                var dash = range.IndexOf('-', StringComparison.Ordinal);
                if (!TryValue(dash < 0 ? range : range[..dash], min, names, out low) ||
                    !TryValue(dash < 0 ? range : range[(dash + 1)..], min, names, out high))
                {
                    error = $"Bad value in the {label} field: \"{part}\".";
                    return false;
                }

                if (dash < 0 && slash >= 0)
                {
                    // "5/15" means from 5 to the end, every 15.
                    high = max;
                }
            }

            if (low < min || high > max || low > high)
            {
                error = $"The {label} field must be within {min}-{max}: \"{part}\".";
                return false;
            }

            for (var v = low; v <= high; v += step)
            {
                bits |= 1UL << v;
            }
        }

        return true;
    }

    private static bool TryValue(string text, int min, string[]? names, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        var index = names is null ? -1 : Array.IndexOf(names, text.ToUpperInvariant());
        value = index + min;
        return index >= 0;
    }
}
