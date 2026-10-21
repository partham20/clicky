namespace Clicky.Core.Ink;

/// <summary>
/// How the live trail fades: ink stays bright for a moment after it is laid down, then fades, so
/// a circle stays whole while it is drawn but wandering doesn't paint the whole screen. When Talk
/// ends the remaining ink holds a little longer, then fades out together.
/// </summary>
public static class InkFade
{
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(1.6);
    public static readonly TimeSpan Fade = TimeSpan.FromSeconds(0.9);
    public static readonly TimeSpan HoldAfterStop = TimeSpan.FromSeconds(1.2);
    public static readonly TimeSpan FadeAfterStop = TimeSpan.FromSeconds(0.6);

    /// <summary>Opacity (0 to 1) at <paramref name="now"/> of ink laid down at <paramref name="drawnAt"/>.</summary>
    /// <param name="stoppedAt">When the recording stopped, or null while it runs.</param>
    public static double Opacity(TimeSpan drawnAt, TimeSpan now, TimeSpan? stoppedAt)
    {
        if (stoppedAt is not { } stop || now <= stop)
        {
            return Decay(now - drawnAt, Hold, Fade);
        }

        // Ink stops ageing at the stop, then everything fades as one.
        return Decay(stop - drawnAt, Hold, Fade) * Decay(now - stop, HoldAfterStop, FadeAfterStop);
    }

    /// <summary>When the last ink is gone, given when the newest ink was laid down.</summary>
    public static TimeSpan GoneAt(TimeSpan newestDrawnAt, TimeSpan? stoppedAt)
    {
        var live = newestDrawnAt + Hold + Fade;
        return stoppedAt is { } stop ? (live < stop ? live : stop + HoldAfterStop + FadeAfterStop) : live;
    }

    private static double Decay(TimeSpan age, TimeSpan hold, TimeSpan fade)
    {
        if (age <= hold)
        {
            return 1;
        }

        var t = (age - hold) / fade;
        if (t >= 1)
        {
            return 0;
        }

        // Smoothstep, so the fade has no visible start or end.
        return 1 - (t * t * (3 - (2 * t)));
    }
}
